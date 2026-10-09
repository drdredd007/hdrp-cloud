using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SpaceRunner.PlanetTerrain;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Camera-owned native opaque submissions. Prepared meshes participate in HDRP culling and every native pass.</summary>
    public sealed class PlanetNativeSurfaceRenderer : IDisposable
    {
        static readonly ProfilerMarker BeginMarker=new ProfilerMarker("PlanetTerrain.NativeBegin");
        static readonly ProfilerMarker SubmitMarker=new ProfilerMarker("PlanetTerrain.NativeSubmit");
        static readonly ProfilerMarker MaterialMarker=new ProfilerMarker("PlanetTerrain.NativeMaterialBind");
        static readonly ProfilerMarker ScheduleMarker=new ProfilerMarker("PlanetTerrain.NativeSchedule");
        static readonly ProfilerMarker UploadMarker=new ProfilerMarker("PlanetTerrain.NativeUpload");
        static readonly ProfilerMarker MeshMarker=new ProfilerMarker("PlanetTerrain.NativeMeshBuild");
        static readonly ProfilerMarker MeshValidationMarker=new ProfilerMarker("PlanetTerrain.NativeMeshValidation");
        static readonly ProfilerMarker VertexPackMarker=new ProfilerMarker("PlanetTerrain.NativeVertexPack");
        static readonly ProfilerMarker IndexPackMarker=new ProfilerMarker("PlanetTerrain.NativeIndexPack");
        static readonly ProfilerMarker MeshUploadMarker=new ProfilerMarker("PlanetTerrain.NativeMeshUpload");
        static readonly ProfilerMarker RetireMarker=new ProfilerMarker("PlanetTerrain.NativeRetire");
        static readonly ProfilingSampler CoverageSampler=new ProfilingSampler("PlanetTerrain.NativeCoverage");
        [StructLayout(LayoutKind.Sequential)] struct Vertex
        {public Vector3 Position,Normal;public Vector4 Tangent;public Color Weights;public Vector2 Uv0,Uv1,Uv2,Uv3;}
        // Packing is synchronous: completed patch arrays remain owned by the caller
        // until upload finishes. Burst removes managed per-vertex array access and
        // arithmetic without changing the sampling, cell topology or publication.
        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
        struct PackVerticesJob : IJob
        {
            [ReadOnly]public NativeArray<float3> Positions,Normals,Offsets;
            [ReadOnly]public NativeArray<SurfaceAttributes> Attributes;
            [WriteOnly]public NativeArray<Vertex> Vertices;
            public NativeArray<float3> Extrema;
            public NativeArray<int> Invalid;
            public void Execute()
            {
                var minimum=new float3(float.PositiveInfinity);var maximum=new float3(float.NegativeInfinity);
                for(int i=0;i<Positions.Length;i++)
                {
                    var p=Positions[i];var n=math.normalizesafe(Normals[i],new float3(0,1,0));var offset=Offsets[i];var attributes=Attributes[i];
                    if(!math.all(math.isfinite(p))||!math.all(math.isfinite(n))||!math.all(math.isfinite(offset)))
                    {Invalid[0]=1;return;}
                    minimum=math.min(minimum,p);maximum=math.max(maximum,p);
                    var tangent=math.normalizesafe(new float3(1,0,0)-n*n.x,new float3(0,0,1));
                    var weights=(attributes.Channels&SurfaceChannels.MaterialWeights)!=0?attributes.MaterialWeights:new float4(0,0,1,0);
                    Vertices[i]=new Vertex {Position=(Vector3)p,Normal=(Vector3)n,Tangent=new Vector4(tangent.x,tangent.y,tangent.z,-1),
                        Uv0=new Vector2(offset.x,offset.y),Uv1=new Vector2(offset.z,0),Uv2=new Vector2(attributes.ErosionData.x,attributes.ErosionData.y),
                        Uv3=new Vector2(attributes.ErosionData.z,attributes.ErosionData.w),Weights=new Color(weights.x,weights.y,weights.z,weights.w)};
                }
                Extrema[0]=minimum;Extrema[1]=maximum;
            }
        }
        sealed class Cell
        {
            public Mesh Mesh;public Matrix4x4 Previous;public bool HasPrevious;int references;
            public void Retain(){references++;}
            public void Release(){if(--references==0)CoreUtils.Destroy(Mesh);}
        }
        sealed class Bank
        {
            public PlanetDefinition Definition;public PlanetSurfaceFrame Frame;public PlanetNativeSurfaceSettings Settings;public bool RequiredMasks;
            public int2 Centre;public int Reused;
            public readonly List<Cell> Cells=new List<Cell>();
            public readonly Dictionary<int2,Cell> ByKey=new Dictionary<int2,Cell>();
            public void Add(int2 key,Cell cell){ByKey.Add(key,cell);cell.Retain();Cells.Add(cell);}
            public void Dispose(){foreach(var cell in Cells)cell.Release();Cells.Clear();ByKey.Clear();}
        }
        sealed class Pending
        {
            public Bank Bank;public List<PlanetRenderPatchRequest> Requests=new List<PlanetRenderPatchRequest>();
            public readonly List<int2> Missing=new List<int2>();
            public int Next,Side,Built;public bool Failed,Ready;
            public bool Completed {get{for(int i=Built;i<Requests.Count;i++)if(!Requests[i].IsCompleted)return false;return true;}}
            public void Dispose(){foreach(var request in Requests)request.Dispose();Requests.Clear();Bank.Dispose();}
        }
        readonly PlanetFarPass owner;
        readonly MaterialPropertyBlock sharedProperties=new MaterialPropertyBlock();
        Bank active;Pending pending;
        readonly List<Pending> abandoned=new List<Pending>();
        readonly List<Bank> retired=new List<Bank>();
        Material material,coverageMaterial;Shader materialShader;
        RTHandle coverage,coverageDepth;int coverageSlices;TextureDimension coverageDimension;bool submittedThisRendering;
        bool disposed;
        PlanetDefinition? stagedRevision;bool stagedEmpty;
        int stagedPreparationFrame=int.MinValue;
        public int PatchCount=>active?.Cells.Count??0;
        /// <summary>Meshes retained from the preceding bank in the currently published bank.</summary>
        public int ReusedPatchCount=>active?.Reused??0;
        public long MeshBuildCount {get;private set;}
        public bool IsReady=>active!=null;
        public bool IsRefining=>pending!=null;
        public int ReservedPatchCount
        {
            get{int result=active?.Cells.Count??0;result+=pending==null?0:pending.Side*pending.Side;
                foreach(var item in abandoned)result+=item.Side*item.Side;foreach(var item in retired)result+=item.Cells.Count;return result;}
        }
        public string Status {get;private set;}="Waiting for native terrain";
        public RTHandle Coverage=>coverage;
        public PlanetSurfaceDescriptor ActiveSurface=>active?.Definition.Surface??default;
        public PlanetNativeSurfaceRenderer(PlanetFarPass owner)
        {
            this.owner=owner??throw new ArgumentNullException(nameof(owner));
            RenderPipelineManager.beginCameraRendering+=BeginCamera;
            RenderPipelineManager.endFrameRendering+=EndFrame;
        }
        static bool SameSurface(PlanetDefinition a,PlanetDefinition b)=>a.Seed==b.Seed&&a.GeneratorVersion==b.GeneratorVersion&&a.Radius==b.Radius&&
            a.Relief==b.Relief&&a.Surface.Equals(b.Surface);
        static bool SameSettings(in PlanetNativeSurfaceSettings a,in PlanetNativeSurfaceSettings b)=>
            a.PatchSize==b.PatchSize&&a.ReceiverHalfSize==b.ReceiverHalfSize&&a.ShadowHalfSize==b.ShadowHalfSize&&
            a.RecenterDistance==b.RecenterDistance&&a.Resolution==b.Resolution&&a.PatchesPerFrame==b.PatchesPerFrame&&a.MaximumResidentPatches==b.MaximumResidentPatches;
        bool HasBaseMap(PlanetDefinition definition)=>owner.BaseMapOverride||PlanetSurfaceDataRegistry.TryGetBaseColour(definition.Surface,out _);
        bool RequiresMaterialWeights(PlanetDefinition definition)=>owner.NativeMaterialSettings&&owner.NativeMaterialSettings.RequireMaterialWeights&&!HasBaseMap(definition);
        public bool Covers(PlanetDefinition definition,double3 planetLocalPosition,double margin=0)
        {
            if(active==null||!SameSurface(active.Definition,definition)||RequiresMaterialWeights(definition)&&!active.RequiredMasks)return false;
            if(math.dot(planetLocalPosition,active.Frame.Up)<=0)return false;
            var p=active.Frame.ToLocal(planetLocalPosition);
            return math.abs(p.x-active.Centre.x*active.Settings.PatchSize)<=active.Settings.ReceiverHalfSize-margin&&
                math.abs(p.z-active.Centre.y*active.Settings.PatchSize)<=active.Settings.ReceiverHalfSize-margin;
        }
        void BeginCamera(ScriptableRenderContext context,Camera camera)
        {
            if(disposed||camera!=owner.Observer)return;
            using(BeginMarker.Auto())BeginCameraCore(context,camera);
        }
        void BeginCameraCore(ScriptableRenderContext context,Camera camera)
        {
            submittedThisRendering=false;
            if(!owner.Enabled||!owner.EnableLocalSurface||!owner.NativeMaterialSettings)return;
            if(!owner.NativeMaterialSettings.IsValid||!owner.Definition.IsValid)
            {Status="Assigned native terrain palette or definition is invalid";return;}
            var settings=owner.NativeSurfaceSettings;
            if(!settings.IsValid){Status="Invalid native terrain quality settings";return;}
            var target=owner.LocalSurfaceTarget??owner.CameraPosition;
            var q=(double4)((quaternion)owner.PlanetRotation).value;
            var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),target-owner.Definition.Center);
            if(!math.all(math.isfinite(localCamera))||math.length(localCamera)-owner.Definition.Radius>20000)return;
            if(stagedRevision.HasValue){var candidate=stagedRevision.Value;candidate.Center=owner.Definition.Center;PrepareRevision(candidate);}
            else Prepare(owner.Definition,localCamera,settings,RequiresMaterialWeights(owner.Definition));
            if(active==null)return;
            if(RequiresMaterialWeights(active.Definition)&&!active.RequiredMasks)return;
            var shader=owner.NativeMaterialSettings.NativeShader;
            if(!shader)shader=Resources.Load<Shader>("PlanetTerrainLit");
            if(!shader||!shader.isSupported){Status="Native terrain shader is unavailable";return;}
            if(!material||materialShader!=shader)
            {CoreUtils.Destroy(material);material=CoreUtils.CreateEngineMaterial(shader);materialShader=shader;material.enableInstancing=true;}
            var frame=active.Frame;
            // Parent supplies a coherent floating, Universe-oriented render basis. Subtract doubles before float conversion.
            var translation=owner.Definition.Center-owner.CameraPosition+PlanetField.Rotate(q,frame.Position);
            var matrix=Matrix4x4.TRS(camera.transform.position+(Vector3)(float3)translation,
                owner.PlanetRotation*(Quaternion)new quaternion((float4)frame.Rotation),Vector3.one);
            bool historyValid=RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline&&pipeline.IsPlanetObjectMotionHistoryValid(camera);
            // Every mesh is relative to the same bank frame. Palette, spherical colour,
            // texture phase and rotation are camera/bank constants, not cell properties.
            // Keep each cell's previous transform separate for object motion vectors.
            using(MaterialMarker.Auto())
            {
                sharedProperties.Clear();PlanetTerrainMaterialBinding.Bind(sharedProperties,owner.NativeMaterialSettings,frame.Position,owner.PlanetRotation,active.Definition.Surface);
                WorldOrogenBaseMapBinding.Bind(sharedProperties,owner.BaseMapColour,owner.PlanetRotation,frame.Position,owner.Definition.Radius);
                PlanetGlobalColorSettings.Bind(sharedProperties,owner.EffectiveGlobalColor,(float)owner.Altitude,owner.PlanetRotation);
                sharedProperties.SetVector("_PlanetGlobalColorAnchor",(Vector3)(float3)(frame.Position/owner.Definition.Radius));
                sharedProperties.SetFloat("_PlanetGlobalColorInverseRadius",(float)(1/owner.Definition.Radius));
            }
            using(SubmitMarker.Auto())foreach(var cell in active.Cells)
            {
                bool objectHistory=cell.HasPrevious&&historyValid;
                var parameters=new RenderParams(material)
                {
                    camera=camera,matProps=sharedProperties,worldBounds=WorldBounds(cell.Mesh.bounds,matrix),
                    shadowCastingMode=ShadowCastingMode.On,receiveShadows=true,
                    motionVectorMode=objectHistory?MotionVectorGenerationMode.Object:MotionVectorGenerationMode.Camera,
                    lightProbeUsage=LightProbeUsage.BlendProbes,renderingLayerMask=uint.MaxValue,layer=0
                };
                Graphics.RenderMesh(parameters,cell.Mesh,0,matrix,objectHistory?cell.Previous:matrix);
                cell.Previous=matrix;cell.HasPrevious=true;
            }
            submittedThisRendering=active.Cells.Count>0;
        }
        /// <summary>Projected ownership and closest metric endpoint from precisely the published native meshes.</summary>
        public bool DrawCoverage(CustomPassContext context)
        {
            if(disposed||context.hdCamera.camera!=owner.Observer)return false;
            // Explicit coverage draws must obey the ordinary native renderer's visibility gates.
            // Its submissions use layer 0; a custom DrawMesh does not apply Camera.cullingMask.
            if(!submittedThisRendering||active==null||(owner.Observer.cullingMask&1)==0||
                !context.hdCamera.frameSettings.IsEnabled(FrameSettingsField.OpaqueObjects))
            {ReleaseCoverage();return false;}
            int width=context.hdCamera.actualWidth,height=context.hdCamera.actualHeight;
            if(coverage==null||coverageDepth==null||coverageSlices!=TextureXR.slices||coverageDimension!=TextureXR.dimension)
            {
                ReleaseCoverage();coverageSlices=TextureXR.slices;coverageDimension=TextureXR.dimension;
                // Both attachments follow the shared physical RTHandle allocation, which
                // can exceed this camera's viewport. The depth is native terrain only:
                // foreground props must not erase its ownership of the coarse approximation.
                coverage=RTHandles.Alloc(Vector2.one,slices:TextureXR.slices,dimension:TextureXR.dimension,
                    colorFormat:UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat,
                    filterMode:FilterMode.Point,useDynamicScale:true,name:"Native planet ownership + metric endpoint");
                coverageDepth=RTHandles.Alloc(Vector2.one,slices:TextureXR.slices,dimension:TextureXR.dimension,
                    depthBufferBits:DepthBits.Depth32,filterMode:FilterMode.Point,useDynamicScale:true,
                    name:"Native planet ownership depth");
            }
            if(!coverageMaterial)
            {
                var shader=Resources.Load<Shader>("PlanetNativeCoverage");
                if(!shader||!shader.isSupported)throw new InvalidOperationException("Native planet ownership shader is unavailable.");
                coverageMaterial=CoreUtils.CreateEngineMaterial(shader);
            }
            using(new ProfilingScope(context.cmd,CoverageSampler)) {
            CoreUtils.SetRenderTarget(context.cmd,coverage,coverageDepth,ClearFlag.All,Color.clear);
            context.cmd.SetViewport(new Rect(0,0,width,height));
            foreach(var cell in active.Cells)context.cmd.DrawMesh(cell.Mesh,cell.Previous,coverageMaterial,0,0);
            }
            return true;
        }
        void ReleaseCoverage()
        {coverage?.Release();coverageDepth?.Release();coverage=coverageDepth=null;coverageSlices=0;coverageDimension=default;}
        /// <summary>Polls completed jobs and schedules bounded work; normal preparation never waits for a worker.</summary>
        public bool PrepareRevision(PlanetDefinition candidate)
        {
            if(disposed||!candidate.IsValid||candidate.GeneratorVersion!=3)return false;
            if(stagedRevision.HasValue&&!SameSurface(stagedRevision.Value,candidate))CancelRevision();
            if(!stagedRevision.HasValue&&pending!=null){abandoned.Add(pending);pending=null;}
            stagedRevision=candidate;
            // Reserve publication bookkeeping before the fixed-tick commit.
            if(active!=null&&retired.Capacity==retired.Count)retired.Capacity=retired.Count+8;
            if(!NeedsRevisionBank(candidate,out var localTarget))
            {if(pending!=null){abandoned.Add(pending);pending=null;}stagedEmpty=true;return true;}
            stagedEmpty=false;
            if(!owner.NativeMaterialSettings.IsValid||!owner.NativeSurfaceSettings.IsValid){Status="Invalid staged native terrain settings";return false;}
            if(stagedPreparationFrame!=Time.frameCount)
            {stagedPreparationFrame=Time.frameCount;Prepare(candidate,localTarget,owner.NativeSurfaceSettings,RequiresMaterialWeights(candidate),false);}
            return RevisionReady(candidate.Surface);
        }
        bool NeedsRevisionBank(PlanetDefinition candidate,out double3 localTarget)
        {
            var q=(double4)((quaternion)owner.PlanetRotation).value;
            localTarget=PlanetField.Rotate(new double4(-q.xyz,q.w),(owner.LocalSurfaceTarget??owner.CameraPosition)-candidate.Center);
            return owner.EnableLocalSurface&&owner.NativeMaterialSettings&&math.all(math.isfinite(localTarget))&&math.length(localTarget)-candidate.Radius<=20000;
        }
        public bool RevisionReady(PlanetSurfaceDescriptor descriptor)
        {
            if(!stagedRevision.HasValue||!stagedRevision.Value.Surface.Equals(descriptor))return false;
            if(active!=null&&retired.Count==retired.Capacity)return false;
            if(stagedEmpty)return !NeedsRevisionBank(stagedRevision.Value,out _);
            return pending!=null&&pending.Ready&&pending.Bank.Definition.Surface.Equals(descriptor)&&owner.NativeMaterialSettings&&owner.NativeMaterialSettings.IsValid&&
                SameSettings(pending.Bank.Settings,owner.NativeSurfaceSettings)&&pending.Bank.RequiredMasks==RequiresMaterialWeights(stagedRevision.Value);
        }
        public bool CommitRevision(PlanetSurfaceDescriptor descriptor)
        {
            if(!RevisionReady(descriptor))return false;
            if(active!=null)retired.Add(active);
            if(stagedEmpty)active=null;
            else{active=pending.Bank;pending.Requests.Clear();pending=null;}
            stagedRevision=null;stagedEmpty=false;Status=active==null?"Committed terrain outside native interest":"Committed native terrain revision";return true;
        }
        public void CancelRevision()
        {
            if(!stagedRevision.HasValue)return;
            if(pending!=null){abandoned.Add(pending);pending=null;}
            stagedRevision=null;stagedEmpty=false;
        }
        public void Prepare(PlanetDefinition definition,double3 localCamera,PlanetNativeSurfaceSettings settings,bool requireMasks,bool publish=true)
        {
            if(disposed)throw new ObjectDisposedException(nameof(PlanetNativeSurfaceRenderer));
            if(!definition.IsValid||!settings.IsValid||!math.all(math.isfinite(localCamera))||math.lengthsq(localCamera)<=0)return;
            // Source-colour maps replace the layered palette; their height-only tiles do not carry GRSS masks.
            requireMasks=requireMasks&&!HasBaseMap(definition);
            PollAbandoned();
            if(pending!=null&&(!SameSurface(pending.Bank.Definition,definition)||!SameSettings(pending.Bank.Settings,settings)||pending.Bank.RequiredMasks!=requireMasks))
            {abandoned.Add(pending);pending=null;}
            bool changed=active==null||!SameSurface(active.Definition,definition)||!SameSettings(active.Settings,settings)||active.RequiredMasks!=requireMasks;
            var delta=active==null?default:active.Frame.ToLocal(localCamera);
            bool needsBank=pending==null&&(changed||math.dot(localCamera,active.Frame.Up)<=0||
                math.abs(delta.x-active.Centre.x*settings.PatchSize)>settings.RecenterDistance||
                math.abs(delta.z-active.Centre.y*settings.PatchSize)>settings.RecenterDistance);
            if(definition.GeneratorVersion==3&&(needsBank||pending!=null&&!pending.Ready)&&
                !PlanetSurfaceDataRegistry.TryPrepareForRendering(definition.Surface,out var preparation))
            {Status="Native terrain waits for render filtering: "+preparation;return;}
            if(needsBank)
            {
                int half=(int)math.ceil(settings.ShadowHalfSize/settings.PatchSize);
                int side=half*2;
                if(ReservedPatchCount+side*side>settings.MaximumResidentPatches)
                {
                    int transition=(active?.Cells.Count??0)+side*side;
                    Status=transition>settings.MaximumResidentPatches?
                        "Native terrain retains previous quality: replacement needs resident budget "+transition:
                        "Native terrain waits for retired workers within the residency budget";return;
                }
                // Reuse only identical frame/key sampling. Small authored recenter
                // thresholds keep the previous exact-centre rebuilding behaviour.
                bool reuse=!changed&&settings.RecenterDistance>=settings.PatchSize*.5&&
                    math.dot(localCamera,active.Frame.Up)>0&&TryChartCentre(delta,settings.PatchSize,half,out _);
                PlanetSurfaceFrame frame;int2 centre;
                if(reuse){frame=active.Frame;TryChartCentre(delta,settings.PatchSize,half,out centre);}
                else
                {
                    var radial=math.normalize(localCamera);
                    var address=new PlanetSurfaceAddress {Latitude=math.degrees(math.asin(math.clamp(radial.y,-1,1))),Longitude=math.degrees(math.atan2(radial.z,radial.x))};
                    if(!PlanetSurfaceCoordinates.TryResolve(definition,address,out frame)){Status="Native terrain surface is not ready";return;}
                    centre=int2.zero;
                }
                pending=new Pending {Bank=new Bank {Definition=definition,Frame=frame,Settings=settings,RequiredMasks=requireMasks,Centre=centre},Side=side};
                for(int z=-half;z<half;z++)for(int x=-half;x<half;x++)
                {
                    var key=centre+new int2(x,z);
                    if(reuse&&active.ByKey.TryGetValue(key,out var cell))
                    {pending.Bank.Add(key,cell);pending.Bank.Reused++;}
                    else pending.Missing.Add(key);
                }
            }
            if(pending==null)return;
            if(pending.Ready)return;
            int total=pending.Side*pending.Side;
            using(ScheduleMarker.Auto())for(int i=0;i<settings.PatchesPerFrame&&pending.Next<pending.Missing.Count;i++)
            {
                int id=pending.Next++;
                pending.Requests.Add(PlanetRenderPatch.Schedule(definition,pending.Bank.Frame,pending.Missing[id],
                    settings.PatchSize,settings.Resolution,requireMasks?SurfaceChannels.MaterialWeights:SurfaceChannels.None));
            }
            // Upload only a bounded number of cells per preparation. Publishing a whole
            // bank does not require uploading every mesh in a single camera frame.
            using(UploadMarker.Auto())for(int uploaded=0;uploaded<settings.PatchesPerFrame&&pending.Built<pending.Requests.Count;uploaded++)
            {
                var request=pending.Requests[pending.Built];if(!request.IsCompleted)break;
                using(var patch=request.Complete())
                {
                    pending.Built++;
                    if(patch.GeometryStatus!=SurfaceSampleStatus.Ready||patch.AttributeStatus!=SurfaceSampleStatus.Ready){pending.Failed=true;break;}
                    pending.Bank.Add(patch.Key,new Cell {Mesh=CreateMesh(patch)});MeshBuildCount++;
                }
            }
            if(pending.Failed)
            {Status="Native terrain bank rejected incomplete geometry or required material masks";abandoned.Add(pending);pending=null;return;}
            if(pending.Next!=pending.Missing.Count||pending.Built!=pending.Requests.Count||pending.Bank.Cells.Count!=total)
            {Status="Preparing native terrain "+pending.Bank.Cells.Count+"/"+total;return;}
            if(!publish){pending.Ready=true;pending.Requests.Clear();Status="Native terrain revision staged";return;}
            if(active!=null)retired.Add(active);
            active=pending.Bank;pending.Requests.Clear();pending=null;
            Status="Native terrain: "+active.Cells.Count+" meshes ("+active.Reused+" reused)";
        }
        static bool TryChartCentre(double3 local,double size,int half,out int2 centre)
        {
            centre=default;var candidate=math.round(new double2(local.x,local.z)/size);
            // This is the same conservative per-key limit enforced by Schedule,
            // checked before conversion to integers or creating output arrays.
            var maximum=math.max(math.abs(candidate-half),math.abs(candidate+half-1));
            if(!math.all(math.isfinite(candidate))||math.any(maximum*size+size>8192))return false;
            centre=(int2)candidate;return true;
        }
        public static Mesh CreateMesh(PlanetRenderPatch patch)
        {
            using var profile=MeshMarker.Auto();
            using(MeshValidationMarker.Auto()) {
            if(patch==null||patch.GeometryStatus!=SurfaceSampleStatus.Ready||patch.AttributeStatus!=SurfaceSampleStatus.Ready)
                throw new ArgumentException("Only a complete immutable render patch can be published.");
            }
            int count=patch.Positions.Length;
            using var vertices=new NativeArray<Vertex>(count,Allocator.TempJob,NativeArrayOptions.UninitializedMemory);
            using var extrema=new NativeArray<float3>(2,Allocator.TempJob,NativeArrayOptions.UninitializedMemory);
            using var invalid=new NativeArray<int>(1,Allocator.TempJob);
            using(VertexPackMarker.Auto())
            {
                new PackVerticesJob {Positions=patch.Positions,Normals=patch.Normals,Offsets=patch.PlanetOffsets,Attributes=patch.Attributes,
                    Vertices=vertices,Extrema=extrema,Invalid=invalid}.Run();
                if(invalid[0]!=0)throw new ArgumentException("Native mesh data must be finite.");
            }
            var minimum=extrema[0];var maximum=extrema[1];
            int resolution=patch.Resolution;var indices=new ushort[resolution*resolution*6];int next=0;
            using(IndexPackMarker.Auto())for(int z=0;z<resolution;z++)for(int x=0;x<resolution;x++)
            {int a=z*(resolution+1)+x,b=a+1,c=a+resolution+1,d=c+1;indices[next++]=(ushort)a;indices[next++]=(ushort)c;indices[next++]=(ushort)b;indices[next++]=(ushort)b;indices[next++]=(ushort)c;indices[next++]=(ushort)d;}
            var mesh=new Mesh {name="Native planet cell "+patch.Key,hideFlags=HideFlags.HideAndDontSave};
            using(MeshUploadMarker.Auto()) {
            mesh.SetVertexBufferParams(count,new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3),
                new VertexAttributeDescriptor(VertexAttribute.Normal,VertexAttributeFormat.Float32,3),new VertexAttributeDescriptor(VertexAttribute.Tangent,VertexAttributeFormat.Float32,4),
                new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.Float32,4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2),new VertexAttributeDescriptor(VertexAttribute.TexCoord1,VertexAttributeFormat.Float32,2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2,VertexAttributeFormat.Float32,2),new VertexAttributeDescriptor(VertexAttribute.TexCoord3,VertexAttributeFormat.Float32,2));
            mesh.SetVertexBufferData(vertices,0,0,count);mesh.SetIndexBufferParams(indices.Length,IndexFormat.UInt16);mesh.SetIndexBufferData(indices,0,0,indices.Length);
            var bounds=new Bounds((Vector3)((minimum+maximum)*.5f),(Vector3)(maximum-minimum+new float3(.02f)));
            mesh.subMeshCount=1;mesh.SetSubMesh(0,new SubMeshDescriptor(0,indices.Length,MeshTopology.Triangles){bounds=bounds,vertexCount=count},MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds=bounds;mesh.UploadMeshData(true);
            }
            return mesh;
        }
        public static Bounds WorldBounds(Bounds bounds,Matrix4x4 matrix)
        {
            var e=bounds.extents;var x=matrix.MultiplyVector(new Vector3(e.x,0,0));var y=matrix.MultiplyVector(new Vector3(0,e.y,0));var z=matrix.MultiplyVector(new Vector3(0,0,e.z));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center),new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),
                Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y),Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z))*2);
        }
        void PollAbandoned()
        {for(int i=abandoned.Count-1;i>=0;i--)if(abandoned[i].Completed){abandoned[i].Dispose();abandoned.RemoveAt(i);}}
        void EndFrame(ScriptableRenderContext context,Camera[] cameras)
        {using var profile=RetireMarker.Auto();foreach(var bank in retired)bank.Dispose();retired.Clear();PollAbandoned();}
        public void Dispose()
        {
            if(disposed)return;disposed=true;RenderPipelineManager.beginCameraRendering-=BeginCamera;RenderPipelineManager.endFrameRendering-=EndFrame;
            pending?.Dispose();pending=null;foreach(var item in abandoned)item.Dispose();abandoned.Clear();active?.Dispose();active=null;
            stagedRevision=null;
            foreach(var bank in retired)bank.Dispose();retired.Clear();CoreUtils.Destroy(material);material=null;
            ReleaseCoverage();CoreUtils.Destroy(coverageMaterial);coverageMaterial=null;
        }
    }
}
