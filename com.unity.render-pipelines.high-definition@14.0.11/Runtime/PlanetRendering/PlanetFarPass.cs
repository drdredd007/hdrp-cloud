using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.HighDefinition;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    // Owned by one observer. Never installs an enormous float Transform into the scene.
    [Serializable]
    public sealed class PlanetFarPass : CustomPass
    {
        static readonly List<PlanetFarPass> liveOwners=new List<PlanetFarPass>();
        public static PlanetFarPass[] LiveOwners=>liveOwners.ToArray();
        PlanetDefinition? stagedSurface;SurfaceScatterExclusions stagedScatterExclusions;
        PlanetSurfaceRegionFilterLease activeFilter,stagedFilter,retiredFilter;
        PlanetSurfaceDescriptor activeFilterDescriptor;
        public SurfaceRegionFilterStatus RegionalFilteringStatus {get;private set;}
        public bool FilteringPending=>Definition.GeneratorVersion==3&&RegionalFilteringStatus==SurfaceRegionFilterStatus.Pending;
        public Camera Observer;
        public Shader PlanetShader,CompositeShader;
        public PlanetDefinition Definition;
        public double3 CameraPosition;
        public double3? LocalSurfaceTarget;
        public Quaternion PlanetRotation=Quaternion.identity;
        public Vector3 LightDirection;
        public Color LightColor=Color.white;
        public float LightLux=50000;
        public bool Enabled=true;
        [NonSerialized] public PlanetFarPass OccludingPass;
        bool renderedThisFrame,renderedNear;
        [NonSerialized] public PlanetLayerDepth LayerDepth;
        public float LayerWeight=1;
        public void ReleaseResources()=>Cleanup();
        // Aerial perspective and sun transmittance from HDRP's PhysicallyBasedSky, applied only while the
        // camera's resolved sky describes this planet (the application configures it, see PlanetAtmosphere).
        public bool EnableAtmosphere=true;
        // Incoming sunlight belongs to this body, even when its view transport is composed elsewhere.
        public PlanetAtmosphereSettings Atmosphere;
        // Light the surface with HDRP directional lights when the camera has any, instead of LightDirection/LightLux.
        public bool UseSceneLights=true;
        public Quaternion CelestialLightRotation=Quaternion.identity;
        readonly PlanetPeriodicTerrainShadows terrainShadows=new PlanetPeriodicTerrainShadows();
        Light terrainShadowSun;
        public PlanetPeriodicTerrainShadows TerrainShadows=>terrainShadows;
        Vector3 TerrainSunDirection()
        {
            if(!UseSceneLights)return LightLux>0?LightDirection.normalized:Vector3.zero;
            if(RenderSettings.sun&&RenderSettings.sun.isActiveAndEnabled&&RenderSettings.sun.type==LightType.Directional)terrainShadowSun=RenderSettings.sun;
            if(!terrainShadowSun||!terrainShadowSun.isActiveAndEnabled)
            {
                terrainShadowSun=null;
                foreach(var candidate in UnityEngine.Object.FindObjectsOfType<Light>())
                    if(candidate.isActiveAndEnabled&&candidate.type==LightType.Directional&&(!terrainShadowSun||candidate.intensity>terrainShadowSun.intensity))terrainShadowSun=candidate;
            }
            return terrainShadowSun?CelestialLightRotation*(-terrainShadowSun.transform.forward):LightLux>0?LightDirection.normalized:Vector3.zero;
        }
        public bool AtmosphereActive {get;private set;}
        // Diagnostics: 0 off; 1 without atmosphere: skirts magenta, uncovered layer pixels green, near-layer pixels tinted red.
        public int DebugView;
        // Optional; PlanetPatchGenerator is otherwise loaded from this module's Resources.
        public ComputeShader Generator;
        public int PatchCount=>geometry.Active.Count;
        public double Altitude=>math.length(CameraPosition-Definition.Center)-Definition.Radius;
        readonly PlanetGpuPatchBackend farPatches=new PlanetGpuPatchBackend(PlanetPatchLayout.Far),nearPatches=new PlanetGpuPatchBackend(PlanetPatchLayout.Local);
        readonly PlanetSurfaceCache geometry;
        public PlanetLodSettings LodSettings=PlanetLodSettings.Default;
        public bool IsRefining=>geometry.IsRefining;
        public bool EnableLocalSurface;
        public Texture2D PeriodicNormalSlopes;
        public PlanetPeriodicSurfaceSettings PeriodicSurfaceSettings;
        public PlanetTerrainMaterialSettings NativeMaterialSettings;
        [NonSerialized] public Texture2DArray BaseMapOverride;
        public Texture2DArray BaseMapColour
        {get{if(BaseMapOverride)return BaseMapOverride;return PlanetSurfaceDataRegistry.TryGetBaseColour(Definition.Surface,out var value)?value:null;}}
        public PlanetNativeSurfaceSettings NativeSurfaceSettings=PlanetNativeSurfaceSettings.Default;
        public PlanetOceanSettings OceanSettings=PlanetOceanSettings.Default;
        readonly PlanetOceanRenderer ocean=new PlanetOceanRenderer();
        public PlanetOceanRenderer OceanRenderer=>ocean;
        public PlanetScatterSettings ScatterSettings;
        public SurfaceScatterPlanetId ScatterInstanceId;
        public bool ScatterMaterialsReady;
        [NonSerialized] public SurfaceScatterExclusions ScatterExclusions;
        public int LocalSurfacePatchCount=>UsesNativeSurface?(nativeGeometry?.PatchCount??0):nearGeometry.Slots.Count;
        public bool HasLocalSurfaceAt(double3 position)
        {
            var q=(double4)((quaternion)PlanetRotation).value;
            var local=PlanetField.Rotate(new double4(-q.xyz,q.w),position-Definition.Center);
            return UsesNativeSurface?nativeGeometry!=null&&nativeGeometry.Covers(Definition,local,0):EnableLocalSurface&&nearGeometry.Covers(Definition,local,128);
        }
        readonly PlanetNearSurfaceCache nearGeometry;
        PlanetNativeSurfaceRenderer nativeGeometry;
        PlanetScatterRenderer scatter;
        PlanetLayerDepth nativeOwnedDepth;
        GraphicsBuffer neutralAttributes;
        // An assigned but invalid native palette is an explicit native error, not a silent legacy-near substitution.
        bool UsesNativeSurface=>EnableLocalSurface&&NativeMaterialSettings;
        public string NativeSurfaceStatus=>nativeGeometry?.Status??"Native terrain is released";
        public PlanetNativeSurfaceRenderer NativeRenderer=>nativeGeometry;
        public PlanetScatterRenderer ScatterRenderer=>scatter;
        public string ScatterStatus=>scatter?.Status??"Scatter is released";
        public PlanetFarPass(){geometry=new PlanetSurfaceCache(farPatches);nearGeometry=new PlanetNearSurfaceCache(nearPatches);nativeGeometry=new PlanetNativeSurfaceRenderer(this);scatter=new PlanetScatterRenderer(this);liveOwners.Add(this);}
        public bool TryPrepareSurfaceRevision(PlanetDefinition candidate,SurfaceScatterExclusions proposedExclusions,out string status)
        {
            status="Surface revision is not ready";
            if(!candidate.IsValid||candidate.GeneratorVersion!=3||!Observer||!Enabled||nativeGeometry==null||scatter==null){status="A live native surface owner and valid signed revision are required";return false;}
            if(!PlanetSurfaceDataRegistry.TryAcquire(candidate.Surface,out var lease)){status="Candidate surface snapshot is not registered";return false;}
            using(lease)if(!PlanetSurfaceData.Compatible(candidate,lease.View)){status="Candidate definition and snapshot do not match";return false;}
            if(proposedExclusions!=null&&!proposedExclusions.Planet.Equals(ScatterInstanceId)){status="Candidate exclusions belong to another planet instance";return false;}
            if(retiredFilter!=null){status="The previous derived surface support is waiting for its first committed draw";return false;}
            candidate.Center=Definition.Center;
            if(stagedSurface.HasValue&&!stagedSurface.Value.Surface.Equals(candidate.Surface))CancelSurfaceRevision();
            stagedSurface=candidate;stagedScatterExclusions=proposedExclusions;
            if(stagedFilter!=null&&stagedFilter.IsDisposed){stagedFilter.Dispose();stagedFilter=null;}
            if(stagedFilter==null&&!PlanetSurfaceRegionFiltering.TryAcquire(candidate.Surface,out stagedFilter,out var filterStatus))
            {status="Candidate regional filtering: "+filterStatus;return false;}
            bool filterReady=stagedFilter.IsReady;
            bool nativeReady=nativeGeometry.PrepareRevision(candidate);
            bool scatterReady=scatter.PrepareRevision(candidate,proposedExclusions);
            status=!filterReady?"Candidate regional filtering: "+stagedFilter.Status:nativeReady?(scatterReady?"Whole render revision staged":scatter.Status):nativeGeometry.Status;
            return filterReady&&nativeReady&&scatterReady;
        }
        public bool TryPrepareSurfaceRevision(PlanetDefinition candidate,out string status)=>TryPrepareSurfaceRevision(candidate,ScatterExclusions,out status);
        public bool IsSurfaceRevisionReady(PlanetSurfaceDescriptor descriptor)=>stagedSurface.HasValue&&stagedSurface.Value.Surface.Equals(descriptor)&&
            stagedFilter!=null&&stagedFilter.Status==SurfaceRegionFilterStatus.Ready&&nativeGeometry!=null&&scatter!=null&&nativeGeometry.RevisionReady(descriptor)&&scatter.RevisionReady(descriptor);
        public bool TryCommitSurfaceRevision(PlanetDefinition candidate,out string status)
        {
            status="Whole render revision is not ready";if(!candidate.IsValid||!IsSurfaceRevisionReady(candidate.Surface))return false;
            if(candidate.Radius!=stagedSurface.Value.Radius||candidate.Seed!=stagedSurface.Value.Seed||candidate.Relief!=stagedSurface.Value.Relief||candidate.GeneratorVersion!=3)
            {status="Committed definition differs from the staged immutable field";return false;}
            // Preflight both owners before either swap. Commit never schedules jobs or waits.
            if(!nativeGeometry.CommitRevision(candidate.Surface)||!scatter.CommitRevision(candidate.Surface))throw new InvalidOperationException("Preflighted render revision changed during a main-thread commit.");
            candidate.Center=Definition.Center;Definition=candidate;ScatterExclusions=stagedScatterExclusions;
            retiredFilter=activeFilter;activeFilter=stagedFilter;stagedFilter=null;activeFilterDescriptor=candidate.Surface;
            RegionalFilteringStatus=SurfaceRegionFilterStatus.Ready;
            // Both caches compare the immutable descriptor before drawing and regenerate
            // committed-field roots on that draw's command buffer. Commit does not
            // release GPU storage or perform any generation work.
            stagedSurface=null;stagedScatterExclusions=null;
            status="Native terrain and scatter revision committed";return true;
        }
        public void CancelSurfaceRevision()
        {nativeGeometry?.CancelRevision();scatter?.CancelRevision();stagedFilter?.Dispose();stagedFilter=null;stagedSurface=null;stagedScatterExclusions=null;}
        Material surface,composite;
        MaterialPropertyBlock properties;
        // Layer depth spans metres to thousands of kilometres; float depth keeps reversed-Z precision across it.
        const GraphicsFormat LayerColorFormat=GraphicsFormat.R32G32B32A32_SFloat,LayerDepthFormat=GraphicsFormat.D32_SFloat;
        RenderTexture farBuffer;
        RenderTexture nearBuffer;



        protected override void Setup(ScriptableRenderContext context,CommandBuffer cmd)
        {
            if(!PlanetShader || !CompositeShader)throw new InvalidOperationException("Planet shaders must be serialized in the sample scene.");
            surface=CoreUtils.CreateEngineMaterial(PlanetShader);composite=CoreUtils.CreateEngineMaterial(CompositeShader);
            properties=new MaterialPropertyBlock();
            if(nativeGeometry==null)nativeGeometry=new PlanetNativeSurfaceRenderer(this);
            if(scatter==null)scatter=new PlanetScatterRenderer(this);
            if(!liveOwners.Contains(this))liveOwners.Add(this);
        }
        protected override void Execute(CustomPassContext ctx)
        {
            renderedThisFrame=false;renderedNear=false;
            if(!EnableLocalSurface||UsesNativeSurface){nearGeometry.Dispose();ReleaseNearBuffer();}
            if(ctx.hdCamera.camera==Observer)AtmosphereActive=false;
            if(!Enabled || ctx.hdCamera.camera!=Observer || !Definition.IsValid || (Altitude<10000 && !EnableLocalSurface) || Observer.orthographic)return;
            retiredFilter?.Dispose();retiredFilter=null;
            if(Definition.GeneratorVersion==3)
            {
                if(activeFilter!=null&&(activeFilter.IsDisposed||!activeFilterDescriptor.Equals(Definition.Surface))){activeFilter.Dispose();activeFilter=null;}
                if(activeFilter==null)
                {if(PlanetSurfaceRegionFiltering.TryAcquire(Definition.Surface,out activeFilter,out var filterStatus))activeFilterDescriptor=Definition.Surface;else RegionalFilteringStatus=filterStatus;}
                if(activeFilter!=null){_ = activeFilter.IsReady;RegionalFilteringStatus=activeFilter.Status;}
            }
            AtmosphereActive=EnableAtmosphere && PlanetAtmosphere.Matches(ctx.hdCamera,Definition,CameraPosition);
            if(RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline)
                pipeline.BindPlanetCelestialLights(ctx.cmd,Observer);
            var centerRelative=(float3)(Definition.Center-CameraPosition);
            int width=ctx.hdCamera.actualWidth,height=ctx.hdCamera.actualHeight;
            var q=(double4)((quaternion)PlanetRotation).value;
            var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),CameraPosition-Definition.Center);
            var shadowSun=Definition.GeneratorVersion==4?TerrainSunDirection():Vector3.up;
            terrainShadows.Prepare(Definition,Quaternion.Inverse(PlanetRotation)*shadowSun,shadowSun,PeriodicSurfaceSettings,properties);
            bool nativeCoverage=UsesNativeSurface&&nativeGeometry!=null&&nativeGeometry.DrawCoverage(ctx);
            bool hasSea=ocean.WantsRender(this);
            if((nativeCoverage||hasSea)&&LayerDepth==null)
            {if(nativeOwnedDepth==null)nativeOwnedDepth=new PlanetLayerDepth();nativeOwnedDepth.Begin(ctx);}
            var effectiveDepth=LayerDepth??((nativeCoverage||hasSea)?nativeOwnedDepth:null);
            composite.SetFloat("_PlanetHasNativeCoverage",nativeCoverage?1:0);
            if(nativeCoverage)composite.SetTexture("_PlanetNativeCoverage",nativeGeometry.Coverage);
            // Generation is enqueued before the draws below on the same command buffer.
            farPatches.Generator=Generator;nearPatches.Generator=Generator;
            bool filteredSurface=Definition.GeneratorVersion==3;
            if(farPatches.FilterRenderingDetail!=filteredSurface)
            {geometry.Dispose();farPatches.FilterRenderingDetail=filteredSurface;}
            var nearTarget=LocalSurfaceTarget ?? CameraPosition;
            double lodMargin=0;
            if(EnableLocalSurface&&math.length(nearTarget-Definition.Center)-Definition.Radius<20000)
                lodMargin=UsesNativeSurface&&NativeSurfaceSettings.IsValid?
                    System.Math.Sqrt(2)*(NativeSurfaceSettings.ShadowHalfSize+NativeSurfaceSettings.RecenterDistance):
                    System.Math.Sqrt(2)*(PlanetNearSurfaceCache.HalfSize+256);
            // View relevance controls only far refinement. Native receivers/shadow casters and physical
            // surface/scatter demand retain their independent banks and Full canonical sampling.
            var lodLocalRotation=math.mul(math.conjugate((quaternion)PlanetRotation),(quaternion)Observer.transform.rotation);
            var cameraProjection=Observer.projectionMatrix;
            if(PlanetLodView.TryFromProjection(lodLocalRotation,cameraProjection,lodMargin,out var lodView))
                geometry.Update(ctx.cmd,Definition,localCamera,height,lodView,LodSettings);
            else geometry.Update(ctx.cmd,Definition,localCamera,height,Observer.fieldOfView,LodSettings);
            if(EnableLocalSurface && !UsesNativeSurface && math.length(nearTarget-Definition.Center)-Definition.Radius<20000)
                nearGeometry.Update(ctx.cmd,Definition,PlanetField.Rotate(new double4(-q.xyz,q.w),nearTarget-Definition.Center));
            else if(Altitude>30000){nearGeometry.Dispose();ReleaseNearBuffer();}
            if(!farBuffer || farBuffer.width!=width || farBuffer.height!=height)
            {
                ReleaseBuffer();farBuffer=new RenderTexture(width,height,LayerColorFormat,LayerDepthFormat)
                {name="Planet far color + ray distance in metres",filterMode=FilterMode.Point};farBuffer.Create();
            }
            // The scaled layer uses its own projection/depth. Its alpha stores unscaled ray distance.
            var projection=GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(Observer.fieldOfView,(float)width/height,.001f,Mathf.Max(30000,(float)((math.length(Definition.Center-CameraPosition)+Definition.Radius+Definition.Relief)*PlanetField.FarScale*1.1))),true);
            // Construct reversed-Z depth directly. Converting float OpenGL-style depth
            // cancels near/far terms for bodies tens of Mm away, clipping valid surfaces.
            if(SystemInfo.usesReversedZBuffer)
            {
                double near=.001,far=math.max(30000,(math.length(Definition.Center-CameraPosition)+Definition.Radius+Definition.Relief)*PlanetField.FarScale*1.1);
                projection.SetRow(2,new Vector4(0,0,(float)(near/(far-near)),(float)(near*far/(far-near))));
            }
            // Keep HDRP's pixel rays, including lens shift and temporal jitter. Only
            // depth range differs in this scaled layer; a fresh symmetric projection
            // disagrees with sky/media reconstruction and moves surfaces between pixels.
            projection.SetRow(0,ctx.hdCamera.mainViewConstants.projMatrix.GetRow(0));
            projection.SetRow(1,ctx.hdCamera.mainViewConstants.projMatrix.GetRow(1));
            var view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.Rotate(Quaternion.Inverse(Observer.transform.rotation));
            surface.SetMatrix("_FarViewProjection",projection*view);surface.SetMatrix("_PlanetRotation",Matrix4x4.Rotate(PlanetRotation));
            surface.SetVector("_PlanetLightDirection",LightDirection);surface.SetColor("_PlanetLightColor",LightColor.linear);surface.SetFloat("_PlanetLightLux",LightLux);
            properties.SetFloat("_PeriodicNormalsEnabled",Definition.GeneratorVersion==4&&PeriodicNormalSlopes?1:0);
            PlanetPeriodicSurfaceSettings.Bind(properties,PeriodicSurfaceSettings,Definition.GeneratorVersion==4,(float)Altitude);
            if(Definition.GeneratorVersion==4)
            {
                if(PeriodicNormalSlopes)properties.SetTexture("_PeriodicNormalSlopes",PeriodicNormalSlopes);
                properties.SetFloat("_PeriodicNormalCycles",(float)(Definition.Radius/Definition.PeriodicHeight.Value.TileMetres));
                properties.SetMatrix("_PeriodicWorldToLocal",Matrix4x4.Rotate(Quaternion.Inverse(PlanetRotation)));
                properties.SetMatrix("_PeriodicLocalToWorld",Matrix4x4.Rotate(PlanetRotation));
            }
            properties.SetVector("_PlanetCenterRelative",(Vector3)centerRelative);
            WorldOrogenBaseMapBinding.Bind(properties,BaseMapColour,PlanetRotation,double3.zero,Definition.Radius);
            properties.SetFloat("_PlanetAtmosphere",AtmosphereActive?1:0);
            // Camera orchestration enables local surface only for its nearest body.
            // Do not give that environment's SH to other planets, nor gate it on
            // per-pixel coverage/readiness (which would pop while looking or warming).
            properties.SetFloat("_PlanetNativeIndirect",UsesNativeSurface&&EnableLocalSurface?1:0);
            properties.SetFloat("_PlanetUseSceneLights",UseSceneLights?1:0);
            properties.SetVector("_PlanetLightRotation",new Vector4(CelestialLightRotation.x,CelestialLightRotation.y,CelestialLightRotation.z,CelestialLightRotation.w));
            properties.SetFloat("_PlanetOwnAir",Atmosphere.IsValid?1:0);
            if(Atmosphere.IsValid)
            {
                float h=PlanetMediaMath.ScaleHeight(Atmosphere.AirMaximumAltitude),ah=PlanetMediaMath.ScaleHeight(Atmosphere.AerosolMaximumAltitude);
                properties.SetVector("_PlanetOwnAirExtinction",new Vector4(PlanetMediaMath.Extinction(Atmosphere.AirOpacity.r,h),
                    PlanetMediaMath.Extinction(Atmosphere.AirOpacity.g,h),PlanetMediaMath.Extinction(Atmosphere.AirOpacity.b,h),h));
                properties.SetVector("_PlanetOwnAerosol",new Vector4(PlanetMediaMath.Extinction(Atmosphere.AerosolOpacity,ah),ah,0,0));
                properties.SetVector("_PlanetOwnDimensions",new Vector4((float)Definition.Radius,Atmosphere.Depth,0,0));
            }
            properties.SetFloat("_PlanetDebugView",DebugView);composite.SetFloat("_PlanetDebugView",DebugView);
            properties.SetInteger("_PlanetMainVertexCount",PlanetGpuPatchBackend.Row*PlanetGpuPatchBackend.Row);
            terrainShadows.SetLayerTarget(ctx.cmd,farBuffer,false);ctx.cmd.SetViewport(new Rect(0,0,width,height));
            // Unity-convention depth (clear 1, ZTest LEqual): Unity reverses both for reversed-Z platforms itself.
                ctx.cmd.ClearRenderTarget(true,true,Color.clear,1);
            properties.SetBuffer("_PlanetVertices",farPatches.Vertices);
            properties.SetBuffer("_PlanetParentVertices",farPatches.ParentVertices??farPatches.Vertices);
            properties.SetInteger("_PlanetFilteredSurface",filteredSurface?1:0);
            properties.SetBuffer("_PlanetTriangleIndices",farPatches.Indices);
            properties.SetInteger("_PlanetNativeSurface",Definition.GeneratorVersion==3?1:0);
            if(neutralAttributes==null){neutralAttributes=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,PlanetSurfaceVertexAttributes.Stride);neutralAttributes.SetData(new PlanetSurfaceVertexAttributes[1]);}
            properties.SetBuffer("_PlanetAttributes",farPatches.Attributes??neutralAttributes);
            properties.SetBuffer("_PlanetParentAttributes",farPatches.ParentAttributes??farPatches.Attributes??neutralAttributes);
            properties.SetFloat("_PlanetTerrainPalette",NativeMaterialSettings&&NativeMaterialSettings.IsValid?1:0);
            properties.SetFloat("_LayerToMeters",1000);
            properties.SetMatrix("_FarViewProjection",projection*view);
            properties.SetMatrix("_PlanetRotation",Matrix4x4.Rotate(PlanetRotation));
            properties.SetMatrix("_DetailRotation",Matrix4x4.identity);
            for(int i=0;i<geometry.Active.Count;i++)
            {
                var key=geometry.Active[i];
                var detailOrigin=PlanetSurfaceCache.Pivot(Definition,key);
                properties.SetVector("_DetailOrigin",(Vector3)(float3)(detailOrigin-math.floor(detailOrigin/4096)*4096));
                if(NativeMaterialSettings&&NativeMaterialSettings.IsValid)
                {PlanetTerrainMaterialBinding.Bind(properties,NativeMaterialSettings,detailOrigin,PlanetRotation);properties.SetVector("_DetailOrigin",Vector4.zero);}
                var relative=PlanetField.RelativeScaled(Definition.Center,CameraPosition,PlanetField.Rotate(q,PlanetSurfaceCache.Pivot(Definition,key)));
                properties.SetVector("_PatchOffset",new Vector4((float)relative.x,(float)relative.y,(float)relative.z,0));
                properties.SetInteger("_PlanetBaseVertex",geometry.Slot(key)*farPatches.SlotVertexCount);
                properties.SetInteger("_PlanetStitchMask",geometry.StitchMask(key));
                ctx.cmd.DrawProcedural(farPatches.Indices,Matrix4x4.identity,surface,0,MeshTopology.Triangles,farPatches.PatchIndexCount,1,properties);
            }
            terrainShadows.TraceLayer(ctx.cmd,farBuffer,(projection*view).inverse,(Vector3)(float3)(CameraPosition-Definition.Center),Matrix4x4.Rotate(Quaternion.Inverse(PlanetRotation)),false);
            bool hasNear=EnableLocalSurface && !UsesNativeSurface && nearGeometry.Slots.Count>0 && Altitude<20000;
            if(hasNear)
            {
                if(!nearBuffer || nearBuffer.width!=width || nearBuffer.height!=height)
                {
                    ReleaseNearBuffer();nearBuffer=new RenderTexture(width,height,LayerColorFormat,LayerDepthFormat)
                    {name="Planet local surface color + metric ray distance",filterMode=FilterMode.Point};nearBuffer.Create();
                }
                terrainShadows.SetLayerTarget(ctx.cmd,nearBuffer,true);ctx.cmd.SetViewport(new Rect(0,0,width,height));
                // Unity-convention depth (clear 1, ZTest LEqual): Unity reverses both for reversed-Z platforms itself.
                ctx.cmd.ClearRenderTarget(true,true,Color.clear,1);
                var frame=nearGeometry.Frame;
                properties.SetMatrix("_DetailRotation",Matrix4x4.Rotate((Quaternion)new quaternion((float4)frame.Rotation)));
                properties.SetVector("_DetailOrigin",(Vector3)(float3)(frame.Position-math.floor(frame.Position/4096)*4096));
                var relative=(Definition.Center-CameraPosition)+PlanetField.Rotate(q,frame.Position);
                var localRotation=PlanetRotation*(Quaternion)new quaternion((float4)frame.Rotation);
                properties.SetVector("_PatchOffset",new Vector4((float)relative.x,(float)relative.y,(float)relative.z,0));
                properties.SetFloat("_LayerToMeters",1);
                var nearProjection=GL.GetGPUProjectionMatrix(Matrix4x4.Perspective(Observer.fieldOfView,(float)width/height,.05f,10000),true);
                nearProjection.SetRow(0,ctx.hdCamera.mainViewConstants.projMatrix.GetRow(0));
                nearProjection.SetRow(1,ctx.hdCamera.mainViewConstants.projMatrix.GetRow(1));
                properties.SetMatrix("_FarViewProjection",nearProjection*view);
                properties.SetMatrix("_PlanetRotation",Matrix4x4.Rotate(localRotation));
                properties.SetBuffer("_PlanetVertices",nearPatches.Vertices);
                properties.SetInteger("_PlanetFilteredSurface",0);
                properties.SetBuffer("_PlanetTriangleIndices",nearPatches.Indices);
                properties.SetInteger("_PlanetMainVertexCount",int.MaxValue);
                properties.SetInteger("_PlanetStitchMask",0);
                foreach(var slot in nearGeometry.Slots)
                {
                    properties.SetInteger("_PlanetBaseVertex",slot*nearPatches.SlotVertexCount);
                    ctx.cmd.DrawProcedural(nearPatches.Indices,Matrix4x4.identity,surface,0,MeshTopology.Triangles,nearPatches.PatchIndexCount,1,properties);
                }
                terrainShadows.TraceLayer(ctx.cmd,nearBuffer,(nearProjection*view).inverse,(Vector3)(float3)(CameraPosition-Definition.Center),Matrix4x4.Rotate(Quaternion.Inverse(PlanetRotation)),true);
            }
            terrainShadows.BindComposite(composite,hasNear);
            composite.SetFloat("_PlanetHasAccumulatedDepth",effectiveDepth!=null?1:0);
            composite.SetFloat("_PlanetLayerWeight",LayerWeight);
            if(effectiveDepth!=null)composite.SetTexture("_PlanetAccumulatedDepth",effectiveDepth.Current);
            composite.SetTexture("_PlanetFarBuffer",farBuffer);
            composite.SetFloat("_PlanetHasNear",hasNear?1:0);
            composite.SetFloat("_PlanetAtmosphere",AtmosphereActive?1:0);
            bool occluded=OccludingPass!=null&&OccludingPass.renderedThisFrame&&OccludingPass.Observer==Observer;
            composite.SetFloat("_PlanetHasOccluder",occluded?1:0);
            if(occluded)
            {
                composite.SetTexture("_PlanetOccluderFar",OccludingPass.farBuffer);
                composite.SetTexture("_PlanetOccluderNear",OccludingPass.renderedNear?OccludingPass.nearBuffer:OccludingPass.farBuffer);
                composite.SetFloat("_PlanetOccluderHasNear",OccludingPass.renderedNear?1:0);
            }
            if(hasNear)composite.SetTexture("_PlanetNearBuffer",nearBuffer);
            CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer);
            ctx.cmd.SetViewport(new Rect(0,0,width,height));
            CoreUtils.DrawFullScreen(ctx.cmd,composite);
            renderedThisFrame=true;renderedNear=hasNear;
            if(effectiveDepth!=null)effectiveDepth.Merge(ctx,composite);
            if(hasSea)ocean.Render(ctx,this,effectiveDepth);
            // Published after this camera's layers, consumed by opaque fog and cloud tracing.
            ctx.cmd.SetGlobalTexture("_PlanetWeatherFarDistance",effectiveDepth!=null?effectiveDepth.Current:farBuffer);
            ctx.cmd.SetGlobalTexture("_PlanetWeatherNearDistance",hasNear?nearBuffer:farBuffer);
            ctx.cmd.SetGlobalInt("_PlanetWeatherHasNear",hasNear?1:0);
            ctx.cmd.SetGlobalInt("_PlanetWeatherDepthReady",1);
        }
        void ReleaseBuffer(){if(farBuffer){farBuffer.Release();CoreUtils.Destroy(farBuffer);farBuffer=null;}}
        void ReleaseNearBuffer(){if(nearBuffer){nearBuffer.Release();CoreUtils.Destroy(nearBuffer);nearBuffer=null;}}

        protected override void Cleanup(){CancelSurfaceRevision();liveOwners.Remove(this);geometry.Dispose();nearGeometry.Dispose();nativeGeometry?.Dispose();nativeGeometry=null;scatter?.Dispose();scatter=null;nativeOwnedDepth?.Dispose();nativeOwnedDepth=null;neutralAttributes?.Dispose();neutralAttributes=null;
            activeFilter?.Dispose();activeFilter=null;retiredFilter?.Dispose();retiredFilter=null;
            terrainShadows.Dispose();ocean.Dispose();ReleaseBuffer();ReleaseNearBuffer();CoreUtils.Destroy(surface);CoreUtils.Destroy(composite);}
    }
}
