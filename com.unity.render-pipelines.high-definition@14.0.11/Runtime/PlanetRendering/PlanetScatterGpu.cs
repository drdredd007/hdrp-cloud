using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>GPU placement samples the immutable SurfaceField; CPU uploads only fixed cell addresses and species rules.</summary>
    public sealed class PlanetScatterGpu : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct ExcludedKey {public uint4 Address,Identity;}
        [StructLayout(LayoutKind.Sequential)] struct ExcludedCircle {public uint4 CenterXY,CenterZ,Radius;}
        public const string ResourceName="PlanetScatter";
        public const string ShaderName="SpaceRunner/Planet Scatter Lit";
        public static int InstanceCountByteOffset {get;}=FindInstanceCountByteOffset();
        static int FindInstanceCountByteOffset()
        {
            // Unity 2022.3 exposes auto-properties. Marshal offsets apply to their
            // actual backing fields, whose layout is also used by SetData(args).
            var type=typeof(GraphicsBuffer.IndirectDrawIndexedArgs);
            foreach(var field in type.GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic))
                if(field.FieldType==typeof(uint)&&(field.Name==nameof(GraphicsBuffer.IndirectDrawIndexedArgs.instanceCount)||field.Name=="<instanceCount>k__BackingField"))
                    return Marshal.OffsetOf(type,field.Name).ToInt32();
            throw new NotSupportedException("The platform's indirect argument layout does not expose its instance-count backing field.");
        }
        readonly PlanetDefinition definition;
        readonly SurfaceScatterSpecies species;
        readonly SurfaceScatterPlanetId planet;
        readonly SurfaceTileKey[] cellKeys;
        readonly bool materialsReady;
        readonly float baseRadius;
        readonly PlanetSurfaceGpuData surface;
        readonly PlanetSurfaceGpuFamily surfaceFamily;
        readonly ComputeShader shader;
        readonly int generateKernel,cullKernel,copyHistoryKernel,generateGroupSize;
        GraphicsBuffer cells,excludedKeys,excludedCircles,candidates,poses,lodState,counters,historyCells;
        readonly GraphicsBuffer[] visible=new GraphicsBuffer[4],arguments=new GraphicsBuffer[4];
        readonly MaterialPropertyBlock[] properties=new MaterialPropertyBlock[4];
        Material material;Shader materialShader;
        int keyCount,circleCount;
        AsyncGPUReadbackRequest readiness;
        bool generated,readinessRequested,readinessComplete,disposed;
        uint frame;int generatedCells;
        PlanetScatterGpu preparedHistory;
        public int CandidateCount {get;}
        public int CellCount=>cellKeys.Length;
        public float MeshRadius=>baseRadius;
        public long SurfaceBytes=>surface.EstimatedBytes;
        public static long EstimateSurfaceBytes(in PlanetDefinition definition)=>PlanetSurfaceGpuData.EstimateBytes(definition.Surface);
        public long EstimatedBytes=>EstimateBytes(CellCount,CandidateCount,keyCount,circleCount);
        public GraphicsBuffer Candidates=>candidates;
        public GraphicsBuffer Poses=>poses;
        public GraphicsBuffer Counters=>counters;
        public GraphicsBuffer Visible(int group)=>visible[group];
        public GraphicsBuffer Arguments(int group)=>arguments[group];
        public SurfaceSampleStatus PreparationStatus {get;private set;}=SurfaceSampleStatus.NotReady;
        public string Status {get;private set;}="GPU placement pending";
        public static long EstimateBytes(int cells,int candidates,int keys=0,int circles=0)=>
            checked((long)math.max(1,cells)*20+(long)candidates*(PlanetScatterCandidateGpu.Stride+PlanetScatterPoseGpu.Stride+20)+
                16+4L*GraphicsBuffer.IndirectDrawIndexedArgs.size+(long)math.max(1,keys)*32+(long)math.max(1,circles)*48);
        public PlanetScatterGpu(PlanetDefinition definition,bool materialsReady,SurfaceScatterPlanetId planet,
            SurfaceScatterSpecies species,IReadOnlyList<SurfaceTileKey> cellKeys,float unscaledMeshRadius,SurfaceScatterExclusions exclusions=null)
        {
            if(!definition.IsValid||definition.GeneratorVersion!=3||!planet.IsValid||!species.IsValid||cellKeys==null||cellKeys.Count<1||
                !math.isfinite(unscaledMeshRadius)||unscaledMeshRadius<0)throw new ArgumentException("Scatter requires a captured signed field, instance, valid rules and fixed cells.");
            if(species.NormalSampleMetres>definition.Radius*.25)throw new ArgumentException("Scatter normal support exceeds the surface sampler's radius bound.");
            if(SurfaceScatterSpacing.Validate(species,definition.Radius,out _)!=SurfaceSampleStatus.Ready)
                throw new ArgumentException("Scatter reference-chord spacing exceeds its bounded cross-face neighborhood/proposal work.");
            if(exclusions!=null&&!exclusions.Planet.Equals(planet))throw new ArgumentException("Scatter exclusion state belongs to another planet instance.");
            this.definition=definition;this.materialsReady=materialsReady;this.planet=planet;this.species=species;this.baseRadius=unscaledMeshRadius;
            this.cellKeys=new SurfaceTileKey[cellKeys.Count];var cellData=new int4[cellKeys.Count];
            for(int i=0;i<cellKeys.Count;i++)
            {
                var key=cellKeys[i];if(!key.IsValid||key.Level!=species.FixedLevel)throw new ArgumentException("Placement cells use the species' fixed grid.");
                double count=1L<<key.Level,width=2/count;var low=2*new double2(key.X,key.Y)/count-1;var high=low+width;
                var closest=math.clamp(new double2(0),low,high);
                double capacity=species.DensityPerSquareMetre*definition.Radius*definition.Radius*width*width/math.pow(1+math.lengthsq(closest),1.5);
                if(!math.isfinite(capacity)||capacity>species.CandidatesPerCell)throw new ArgumentException("Fixed cell proposal capacity is insufficient for its reference-sphere density.");
                this.cellKeys[i]=key;cellData[i]=new int4(key.Face,key.Level,key.X,key.Y);
            }
            CandidateCount=checked(cellKeys.Count*species.CandidatesPerCell);
            surface=PlanetSurfaceGpuData.Acquire(definition);
            try
            {
                // All programs share the exact placement/cull/history body. Prune only
                // authorities that cannot execute for this immutable captured snapshot.
                surfaceFamily=surface.PatchFamily;
                shader=Resources.Load<ComputeShader>(FamilyResource(surfaceFamily));
                if(!shader)throw new NotSupportedException("Planet scatter compute shader is unavailable.");
                generateKernel=shader.FindKernel("GenerateCandidates");cullKernel=shader.FindKernel("CullAndPose");copyHistoryKernel=shader.FindKernel("CopyHistory");
                if(!SystemInfo.supportsComputeShaders||!shader.IsSupported(generateKernel)||!shader.IsSupported(cullKernel)||!shader.IsSupported(copyHistoryKernel))
                    throw new NotSupportedException("This device cannot execute the canonical GPU scatter kernels.");
                shader.GetKernelThreadGroupSizes(generateKernel,out uint generationThreads,out uint generationY,out uint generationZ);
                if(generationThreads==0||generationY!=1||generationZ!=1)throw new NotSupportedException("Scatter generation requires a one-dimensional thread group.");
                generateGroupSize=checked((int)generationThreads);
                cells=Buffer(cellData);historyCells=new GraphicsBuffer(GraphicsBuffer.Target.Structured,CellCount,4);
                candidates=new GraphicsBuffer(GraphicsBuffer.Target.Structured,CandidateCount,PlanetScatterCandidateGpu.Stride);
                poses=new GraphicsBuffer(GraphicsBuffer.Target.Structured,CandidateCount,PlanetScatterPoseGpu.Stride);
                lodState=new GraphicsBuffer(GraphicsBuffer.Target.Structured,CandidateCount,4);counters=Buffer(new uint[4]);
                var keyData=new ExcludedKey[exclusions?.Keys.Count??0];keyCount=keyData.Length;
                for(int i=0;i<keyCount;i++){var k=exclusions.Keys[i];keyData[i]=new ExcludedKey{Address=new uint4((uint)k.Cell.Face,(uint)k.Cell.Level,(uint)k.Cell.X,(uint)k.Cell.Y),Identity=new uint4(k.SpeciesId,(uint)k.Slot,0,0)};}
                excludedKeys=Buffer(keyData);var circleData=new ExcludedCircle[exclusions?.Circles.Count??0];circleCount=circleData.Length;
                for(int i=0;i<circleCount;i++){var c=exclusions.Circles[i];circleData[i]=new ExcludedCircle{CenterXY=PlanetSurfaceGpuData.Pair(c.CenterDirection.x,c.CenterDirection.y),
                    CenterZ=PlanetSurfaceGpuData.Pair(c.CenterDirection.z,0),Radius=PlanetSurfaceGpuData.Pair(c.RadiusMetres,0)};}
                excludedCircles=Buffer(circleData);
                for(int i=0;i<4;i++){visible[i]=new GraphicsBuffer(GraphicsBuffer.Target.Append,CandidateCount,4);
                    arguments[i]=new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,1,GraphicsBuffer.IndirectDrawIndexedArgs.size);properties[i]=new MaterialPropertyBlock();}
            }
            catch{Dispose();throw;}
        }
        static string FamilyResource(PlanetSurfaceGpuFamily family)
        {
            switch(family)
            {
                case PlanetSurfaceGpuFamily.Plain:return ResourceName;
                case PlanetSurfaceGpuFamily.Orogen:return "PlanetOrogenScatter";
                case PlanetSurfaceGpuFamily.LegacyStructure:return "PlanetStructureScatter";
                case PlanetSurfaceGpuFamily.Drainage:return "PlanetDrainageScatter";
                case PlanetSurfaceGpuFamily.Landform:return "PlanetLandformScatter";
                default:throw new NotSupportedException("The captured sampler authority has no bounded scatter shader family.");
            }
        }
        static GraphicsBuffer Buffer<T>(T[] values)where T:struct
        {var b=new GraphicsBuffer(GraphicsBuffer.Target.Structured,math.max(1,values.Length),Marshal.SizeOf<T>());try{b.SetData(values.Length==0?new T[1]:values);return b;}catch{b.Dispose();throw;}}
        static void Bits(CommandBuffer cmd,ComputeShader shader,string name,uint4 bits)=>cmd.SetComputeIntParams(shader,name,
            unchecked((int)bits.x),unchecked((int)bits.y),unchecked((int)bits.z),unchecked((int)bits.w));
        public void Generate(CommandBuffer cmd)
        {
            if(generatedCells!=0)throw new InvalidOperationException("Generate is the whole-bank entry point.");GenerateCells(cmd,CellCount);
        }
        public void GenerateCells(CommandBuffer cmd,int count)
        {
            if(disposed)throw new ObjectDisposedException(nameof(PlanetScatterGpu));
            if(count<1||count>CellCount-generatedCells)throw new ArgumentOutOfRangeException(nameof(count));
            surface.Bind(cmd,shader,generateKernel,surfaceFamily);
            Bits(cmd,shader,"_SurfaceRadiusAndNormal",PlanetSurfaceGpuData.Pair(definition.Radius,species.NormalSampleMetres));
            Bits(cmd,shader,"_ScatterPlanet",new uint4((uint)planet.High,(uint)(planet.High>>32),(uint)planet.Low,(uint)(planet.Low>>32)));
            Bits(cmd,shader,"_ScatterDensityHeightMinimum",PlanetSurfaceGpuData.Pair(species.DensityPerSquareMetre,species.MinimumHeight));
            Bits(cmd,shader,"_ScatterHeightMaximumSlopeCosine",PlanetSurfaceGpuData.Pair(species.MaximumHeight,math.cos(math.radians(species.MaximumSlopeDegrees))));
            Bits(cmd,shader,"_ScatterWetness",PlanetSurfaceGpuData.Pair(species.MinimumWetness,species.MaximumWetness));
            Bits(cmd,shader,"_ScatterSpacing",PlanetSurfaceGpuData.Pair(species.MinimumReferenceChordSpacingMetres,SurfaceScatterSpacing.NumericalPadding));
            cmd.SetComputeIntParam(shader,"_ScatterSpecies",unchecked((int)species.SpeciesId));cmd.SetComputeIntParam(shader,"_ScatterSeed",unchecked((int)species.Seed));
            cmd.SetComputeIntParam(shader,"_ScatterSlots",species.CandidatesPerCell);cmd.SetComputeIntParam(shader,"_ScatterCount",CandidateCount);
            cmd.SetComputeIntParam(shader,"_ScatterStart",generatedCells*species.CandidatesPerCell);
            cmd.SetComputeIntParam(shader,"_ScatterEnd",(generatedCells+count)*species.CandidatesPerCell);
            var required=species.RequiredChannels|SurfaceChannels.MaterialWeights;
            if(species.MinimumWetness>0||species.MaximumWetness<1)required|=SurfaceChannels.ErosionData;
            cmd.SetComputeIntParam(shader,"_ScatterRequiredChannels",(int)required);cmd.SetComputeIntParam(shader,"_ScatterExcludeStamps",species.ExcludeSurfaceStamps?1:0);
            cmd.SetComputeIntParam(shader,"_ScatterMaterialsReady",materialsReady?1:0);cmd.SetComputeIntParam(shader,"_ScatterExcludedKeyCount",keyCount);
            cmd.SetComputeIntParam(shader,"_ScatterExcludedCircleCount",circleCount);
            cmd.SetComputeVectorParam(shader,"_ScatterAffinity",(Vector4)species.MaterialAffinity);
            cmd.SetComputeVectorParam(shader,"_ScatterScaleRadius",new Vector4(species.ScaleRange.x,species.ScaleRange.y,baseRadius,0));
            cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterCells",cells);cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterCandidates",candidates);
            cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterPoses",poses);
            cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterLodState",lodState);cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterStatusCounters",counters);
            cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterExcludedKeys",excludedKeys);cmd.SetComputeBufferParam(shader,generateKernel,"_ScatterExcludedCircles",excludedCircles);
            int groups=checked((int)(((long)count*species.CandidatesPerCell+generateGroupSize-1)/generateGroupSize));
            cmd.DispatchCompute(shader,generateKernel,groups,1,1);generatedCells+=count;generated=generatedCells==CellCount;
        }
        public void CopyHistory(CommandBuffer cmd,PlanetScatterGpu previous)
        {
            PrepareHistory(previous);CopyPreparedHistory(cmd);
        }
        /// <summary>Prepare stable-cell remapping while a replacement is held off the live owner.</summary>
        public void PrepareHistory(PlanetScatterGpu previous)
        {
            if(disposed||!generated||previous==null||previous.disposed||!previous.generated||!planet.Equals(previous.planet)||species.SpeciesId!=previous.species.SpeciesId||
                species.FixedLevel!=previous.species.FixedLevel||species.CandidatesPerCell!=previous.species.CandidatesPerCell)
                throw new ArgumentException("History requires matching stable planet/species/cell addresses.");
            var indices=new Dictionary<SurfaceTileKey,int>();for(int i=0;i<previous.cellKeys.Length;i++)indices.Add(previous.cellKeys[i],i);
            var mapping=new int[CellCount];for(int i=0;i<CellCount;i++)mapping[i]=indices.TryGetValue(cellKeys[i],out int old)?old:-1;
            historyCells.SetData(mapping);preparedHistory=previous;
        }
        /// <summary>Transfer the last submitted poses immediately before this bank's first cull.</summary>
        public void CopyPreparedHistory(CommandBuffer cmd)
        {
            var previous=preparedHistory;
            if(disposed||previous==null||previous.disposed)throw new InvalidOperationException("A retained history source must precede the first new-bank draw.");
            frame=previous.frame;
            cmd.SetComputeIntParam(shader,"_ScatterCount",CandidateCount);cmd.SetComputeIntParam(shader,"_ScatterSlots",species.CandidatesPerCell);
            cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterCandidates",candidates);cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterPoses",poses);
            cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterLodState",lodState);cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterHistoryCells",historyCells);
            cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterOldCandidates",previous.candidates);cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterOldPoses",previous.poses);
            cmd.SetComputeBufferParam(shader,copyHistoryKernel,"_ScatterOldLodState",previous.lodState);cmd.DispatchCompute(shader,copyHistoryKernel,(CandidateCount+63)/64,1,1);
            preparedHistory=null;
        }
        /// <summary>One asynchronous readiness read per bank, never a synchronous frame/physics readback.</summary>
        public void RequestReadiness()
        {
            if(disposed||!generated||readinessRequested)throw new InvalidOperationException("Generate one complete bank before requesting readiness.");
            if(!SystemInfo.supportsAsyncGPUReadback)throw new NotSupportedException("Scatter bank publication requires asynchronous GPU readiness queries.");
            readinessRequested=true;
            readiness=AsyncGPUReadback.Request(counters,request=>
            {
                if(disposed)return;
                if(request.hasError){PreparationStatus=SurfaceSampleStatus.IncompatibleData;Status="GPU readiness query failed";}
                else
                {
                    uint missing=request.GetData<uint>()[0];PreparationStatus=missing==0?SurfaceSampleStatus.Ready:SurfaceSampleStatus.NotReady;
                    Status=missing==0?"Canonical GPU candidates ready":"Canonical GPU samples are not ready: "+missing;
                }
                readinessComplete=true;
            });
        }
        public bool PollReadiness()
        {
            return readinessComplete;
        }
        public void PrepareView(CommandBuffer cmd,Camera camera,double3 cameraPosition,Quaternion planetRotation,
            float renderDistance,float shadowDistance,float lodDistance,float hysteresis,bool historyValid,double3? planetCenter=null)
        {
            if(disposed||!generated||!camera)throw new InvalidOperationException("A generated bank and camera are required.");
            var q=(double4)((quaternion)planetRotation).value;
            var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),cameraPosition-(planetCenter??definition.Center));
            Bits(cmd,shader,"_ScatterCameraXY",PlanetSurfaceGpuData.Pair(localCamera.x,localCamera.y));Bits(cmd,shader,"_ScatterCameraZ",PlanetSurfaceGpuData.Pair(localCamera.z,0));
            cmd.SetComputeIntParam(shader,"_ScatterCount",CandidateCount);cmd.SetComputeIntParam(shader,"_ScatterHistoryValid",historyValid?1:0);
            cmd.SetComputeIntParam(shader,"_ScatterFrame",unchecked((int)++frame));cmd.SetComputeIntParam(shader,"_ScatterGeneration",1);
            cmd.SetComputeVectorParam(shader,"_ScatterPlanetRotation",new Vector4(planetRotation.x,planetRotation.y,planetRotation.z,planetRotation.w));
            cmd.SetComputeVectorParam(shader,"_ScatterCameraWorld",camera.transform.position);cmd.SetComputeVectorParam(shader,"_ScatterDistances",new Vector4(renderDistance,shadowDistance,lodDistance,hysteresis));
            var planes=GeometryUtility.CalculateFrustumPlanes(camera);var packed=new Vector4[6];
            for(int i=0;i<6;i++)packed[i]=new Vector4(planes[i].normal.x,planes[i].normal.y,planes[i].normal.z,planes[i].distance);
            cmd.SetComputeVectorArrayParam(shader,"_ScatterFrustum",packed);
            cmd.SetComputeBufferParam(shader,cullKernel,"_ScatterCandidates",candidates);cmd.SetComputeBufferParam(shader,cullKernel,"_ScatterPoses",poses);
            cmd.SetComputeBufferParam(shader,cullKernel,"_ScatterLodState",lodState);
            string[] names={"_ScatterVisibleNear","_ScatterVisibleFar","_ScatterShadowNear","_ScatterShadowFar"};
            for(int i=0;i<4;i++){cmd.SetBufferCounterValue(visible[i],0);cmd.SetComputeBufferParam(shader,cullKernel,names[i],visible[i]);}
            cmd.DispatchCompute(shader,cullKernel,(CandidateCount+63)/64,1,1);
        }
        public Bounds WorldBounds(Camera camera,double3 cameraPosition,Quaternion planetRotation,double3? planetCenter=null)
        {
            var q=(double4)((quaternion)planetRotation).value;var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),cameraPosition-(planetCenter??definition.Center));
            Bounds bounds=default;bool first=true;
            foreach(var cell in cellKeys)
            {
                CubeSurface.TryDirection(cell,new double2(.5),out var center);double reach=0;
                for(int y=0;y<2;y++)for(int x=0;x<2;x++){CubeSurface.TryDirection(cell,new double2(x,y),out var corner);reach=math.max(reach,math.distance(center,corner)*definition.Radius);}
                reach+=definition.Relief+baseRadius*species.ScaleRange.y;
                var world=camera.transform.position+(Vector3)(float3)PlanetField.Rotate(q,center*definition.Radius-localCamera);
                var part=new Bounds(world,Vector3.one*(float)(2*reach));if(first){bounds=part;first=false;}else bounds.Encapsulate(part);
            }
            return bounds;
        }
        public void Submit(Camera camera,PlanetScatterSpecies renderSpecies,Bounds bounds,bool historyValid)
        {
            if(disposed||!renderSpecies||!renderSpecies.IsValid)throw new ArgumentException("Valid native scatter meshes/material are required.");
            var nativeShader=Resources.Load<Shader>("PlanetScatterLit");if(!nativeShader||!nativeShader.isSupported)throw new NotSupportedException("Native indirect scatter shader is unavailable.");
            if(!material||materialShader!=nativeShader){CoreUtils.Destroy(material);material=new Material(renderSpecies.Material){shader=nativeShader,hideFlags=HideFlags.HideAndDontSave};materialShader=nativeShader;}
            material.CopyPropertiesFromMaterial(renderSpecies.Material);material.enableInstancing=true;
            for(int i=0;i<4;i++)
            {
                var mesh=(i&1)==0?renderSpecies.NearMesh:renderSpecies.FarMesh;
                var args=new GraphicsBuffer.IndirectDrawIndexedArgs{indexCountPerInstance=mesh.GetIndexCount(0),instanceCount=0,
                    startIndex=mesh.GetIndexStart(0),baseVertexIndex=(uint)mesh.GetBaseVertex(0),startInstance=0};
                arguments[i].SetData(new[]{args});
                // This offset is taken from the platform's public argument layout.
                GraphicsBuffer.CopyCount(visible[i],arguments[i],InstanceCountByteOffset);
                var p=properties[i];p.Clear();p.SetBuffer("_PlanetScatterPoses",poses);p.SetBuffer("_PlanetScatterVisible",visible[i]);
                var parameters=new RenderParams(material){camera=camera,matProps=p,worldBounds=bounds,layer=0,renderingLayerMask=uint.MaxValue,
                    shadowCastingMode=i<2?ShadowCastingMode.Off:ShadowCastingMode.ShadowsOnly,receiveShadows=true,
                    motionVectorMode=historyValid?MotionVectorGenerationMode.Object:MotionVectorGenerationMode.Camera,lightProbeUsage=LightProbeUsage.BlendProbes};
                Graphics.RenderMeshIndirect(parameters,mesh,arguments[i]);
            }
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;cells?.Dispose();historyCells?.Dispose();excludedKeys?.Dispose();excludedCircles?.Dispose();candidates?.Dispose();poses?.Dispose();lodState?.Dispose();counters?.Dispose();
            foreach(var b in visible)b?.Dispose();foreach(var b in arguments)b?.Dispose();CoreUtils.Destroy(material);PlanetSurfaceGpuData.Release(surface);
        }
    }
}
