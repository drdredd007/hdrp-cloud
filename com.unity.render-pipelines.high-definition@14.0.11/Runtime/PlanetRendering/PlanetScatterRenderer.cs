using System;
using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>One camera/planet owner. Fixed placement cells never inherit terrain render LOD.</summary>
    public sealed class PlanetScatterRenderer : IDisposable
    {
        sealed class SpeciesBank
        {
            public PlanetScatterSpecies Source;
            public SurfaceScatterSpecies Rules;
            public SurfaceTileKey[] Cells;
            public PlanetScatterGpu Gpu;
            public PlanetScatterGpu HistorySource;
            public int GeneratedCells;
        }
        sealed class Bank
        {
            public PlanetDefinition Definition;
            public SurfaceScatterPlanetId Instance;
            public SurfaceContentHash Profile,Exclusions;
            public readonly List<SpeciesBank> Species=new List<SpeciesBank>();
            public PlanetScatterSpecies[] ProfileSources;public SurfaceScatterSpecies[] ProfileRules;
            public int Cells,Candidates;public long Bytes,SurfaceBytes;public float PublishedTime;
            public bool ReadinessRequested,Ready,HistoryPrepared,HistoryPending;
            public Retirement Retirement;
            public void Dispose(){foreach(var item in Species)item.Gpu.Dispose();Species.Clear();}
        }
        sealed class Retirement {public Bank Bank,HistoryConsumer;public bool Complete,Requested;}
        readonly PlanetFarPass owner;
        Bank active,pending;
        readonly List<Retirement> retired=new List<Retirement>();
        bool disposed;
        PlanetDefinition? stagedRevision;SurfaceScatterExclusions stagedExclusions;bool stagedEmpty;
        int generationFrame=int.MinValue,generationCells;
        public int ResidentCells {get;private set;}
        public int ResidentCandidates {get;private set;}
        public long ResidentBytes {get;private set;}
        public int ActiveCandidateCount=>active?.Candidates??0;
        public bool IsReady=>active!=null;
        public bool IsPreparing=>pending!=null;
        public bool HasPendingHistory=>active!=null&&active.HistoryPending;
        public string Status {get;private set;}="Waiting for canonical scatter cells";
        public PlanetSurfaceDescriptor ActiveSurface=>active?.Definition.Surface??default;
        public PlanetScatterRenderer(PlanetFarPass owner)
        {
            this.owner=owner??throw new ArgumentNullException(nameof(owner));
            RenderPipelineManager.beginCameraRendering+=BeginCamera;
            RenderPipelineManager.endFrameRendering+=EndFrame;
        }
        static bool SameSurface(PlanetDefinition a,PlanetDefinition b)=>a.Radius==b.Radius&&a.Relief==b.Relief&&a.Surface.Equals(b.Surface);
        bool SameCapture(Bank bank,SurfaceContentHash profile,SurfaceContentHash exclusions)=>bank!=null&&
            SameSurface(bank.Definition,owner.Definition)&&bank.Instance.Equals(owner.ScatterInstanceId)&&bank.Profile.Equals(profile)&&bank.Exclusions.Equals(exclusions);
        void BeginCamera(ScriptableRenderContext context,Camera camera)
        {
            if(disposed||camera!=owner.Observer)return;
            CollectRetired();
            var settings=owner.ScatterSettings;
            if(!owner.Enabled||!settings||!settings.Enabled){ReleaseBanks();Status="Scatter disabled";return;}
            if(!settings.IsValid||!owner.Definition.IsValid||owner.Definition.GeneratorVersion!=3||!owner.ScatterInstanceId.IsValid)
            {Status="Scatter requires valid native meshes, placement rules, signed field and instance identity";return;}
            if(!owner.ScatterMaterialsReady){Status="Canonical material repair is pending";return;}
            if(!SystemInfo.supportsAsyncGPUReadback){Status="Scatter publication requires asynchronous GPU readiness queries";return;}
            if(!math.all(math.isfinite(owner.CameraPosition))){Status="Scatter camera position is invalid";return;}
            if(stagedRevision.HasValue)
            {
                var candidate=stagedRevision.Value;candidate.Center=owner.Definition.Center;PrepareRevision(candidate,stagedExclusions);
            }
            else PrepareCurrent(settings);
            if(active==null)return;
            DrawActive(camera);
        }
        void PrepareCurrent(PlanetScatterSettings settings)
        {
            var profile=settings.CapturePlacement().ContentDigest;
            var exclusions=owner.ScatterExclusions?.ContentDigest??default;
            if(pending!=null&&!SameCapture(pending,profile,exclusions)){Retire(pending);pending=null;}
            PollPending();
            var q=(double4)((quaternion)owner.PlanetRotation).value;
            var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),owner.CameraPosition-owner.Definition.Center);
            if(!math.all(math.isfinite(localCamera))||math.lengthsq(localCamera)<=0){Status="Scatter camera position is invalid";return;}
            var desired=new List<SpeciesBank>();
            bool collected=TryCollect(settings,owner.Definition,localCamera,desired,out var reason);
            if(!collected)Status=reason;
            else if(desired.Count==0){ReleaseBanks();Status="Camera is outside scatter render and shadow interest";return;}
            else if(pending==null&&(!SameCapture(active,profile,exclusions)||!Covers(active,desired,settings.CellRetentionSeconds)))
                TryBegin(settings,owner.Definition,owner.ScatterExclusions,profile,exclusions,desired);
            if(pending!=null)GeneratePending(settings.MaximumNewCellsPerFrame);
        }
        void DrawActive(Camera camera)
        {
            // Old immutable banks remain coherent until a whole new bank is sample-ready.
            bool history=RenderPipelineManager.currentPipeline is HDRenderPipeline pipeline&&pipeline.IsPlanetObjectMotionHistoryValid(camera);
            var cmd=CommandBufferPool.Get("Canonical scatter cull and native poses");
            try
            {
                if(active.HistoryPending)
                {
                    foreach(var item in active.Species)if(item.HistorySource!=null)item.Gpu.CopyPreparedHistory(cmd);
                    active.HistoryPending=false;
                }
                foreach(var item in active.Species)
                {
                    if(!item.Source||!item.Source.IsValid)continue;
                    var source=item.Source;
                    item.Gpu.PrepareView(cmd,camera,owner.CameraPosition,owner.PlanetRotation,source.RenderDistance,source.ShadowDistance,
                        source.LodDistance,source.LodHysteresis,history,owner.Definition.Center);
                }
                Graphics.ExecuteCommandBuffer(cmd);
                foreach(var item in active.Species)
                    if(item.Source&&item.Source.IsValid)item.Gpu.Submit(camera,item.Source,
                        item.Gpu.WorldBounds(camera,owner.CameraPosition,owner.PlanetRotation,owner.Definition.Center),history);
            }
            finally{CommandBufferPool.Release(cmd);}
        }
        bool TryCollect(PlanetScatterSettings settings,PlanetDefinition definition,double3 localCamera,List<SpeciesBank> result,out string reason)
        {
            reason=null;
            int totalCells=0,totalCandidates=0;
            foreach(var source in settings.Species)
            {
                double reach=source.ShadowDistance+source.UnscaledBoundingRadius*source.ScaleRange.y;
                if(!TryInterest(localCamera,definition.Radius,definition.Relief,reach,out var anchor,out double angular))continue;
                var keys=new List<SurfaceTileKey>();int available=settings.MaximumResidentCells-totalCells;
                if(available<1||!SurfaceScatterCells.TryCollect(anchor,angular,source.FixedLevel,available,keys,out var status))
                {reason="Scatter fixed-cell interest exceeds the resident cell budget";return false;}
                totalCells=checked(totalCells+keys.Count);totalCandidates=checked(totalCandidates+keys.Count*source.CandidatesPerCell);
                if(totalCandidates>settings.MaximumResidentCandidates){reason="Scatter interest exceeds the resident candidate budget";return false;}
                if(keys.Count>0)result.Add(new SpeciesBank{Source=source,Rules=source.Placement,Cells=keys.ToArray()});
            }
            return true;
        }
        /// <summary>Exact conservative cap for every height in the captured bounds; no global relief added to the tangential interest.</summary>
        public static bool TryInterest(double3 camera,double radius,double relief,double reach,out double3 anchor,out double angular)
        {
            anchor=default;angular=0;
            double d=math.length(camera);
            if(!math.all(math.isfinite(camera))||!math.isfinite(d)||d<=0||!math.isfinite(radius)||radius<=0||
                !math.isfinite(relief)||relief<0||!math.isfinite(radius+relief)||!math.isfinite(reach)||reach<0)return false;
            anchor=camera/d;double minimum=math.max(0,radius-relief),maximum=radius+relief;
            if(math.abs(d-math.clamp(d,minimum,maximum))>reach)return false;
            if(reach>=d+minimum){angular=Math.PI;return true;}
            double ratio=math.min(1,reach/d);
            double rho=math.clamp(d*math.sqrt(math.max(0,1-ratio*ratio)),minimum,maximum);
            double gap=math.abs(d-rho);
            // Factored difference avoids cancellation at metre-scale support on huge planets.
            double chordSquared=math.max(0,(reach-gap)*(reach+gap)/(d*rho));
            angular=2*math.asin(math.min(1,math.sqrt(chordSquared)*.5));return true;
        }
        static bool Covers(Bank bank,List<SpeciesBank> desired,float retentionSeconds)
        {
            if(bank==null||bank.Species.Count!=desired.Count)return false;
            foreach(var request in desired)
            {
                SpeciesBank match=null;foreach(var item in bank.Species)if(item.Rules.SpeciesId==request.Rules.SpeciesId){match=item;break;}
                if(match==null||match.Source!=request.Source||match.Gpu.MeshRadius!=request.Source.UnscaledBoundingRadius)return false;
                var retained=new HashSet<SurfaceTileKey>(match.Cells);
                foreach(var key in request.Cells)if(!retained.Contains(key))return false;
                if(match.Cells.Length!=request.Cells.Length&&Time.realtimeSinceStartup-bank.PublishedTime>retentionSeconds)return false;
            }
            return true;
        }
        void TryBegin(PlanetScatterSettings settings,PlanetDefinition definition,SurfaceScatterExclusions state,SurfaceContentHash profile,SurfaceContentHash exclusions,List<SpeciesBank> desired)
        {
            CountResidency();int cells=0,candidates=0;long bytes=0;
            foreach(var item in desired)
            {
                int count=checked(item.Cells.Length*item.Rules.CandidatesPerCell);cells=checked(cells+item.Cells.Length);candidates=checked(candidates+count);
                bytes=checked(bytes+PlanetScatterGpu.EstimateBytes(item.Cells.Length,count,state?.Keys.Count??0,state?.Circles.Count??0));
            }
            long surfaceBytes;
            try{surfaceBytes=PlanetScatterGpu.EstimateSurfaceBytes(definition);}catch(InvalidOperationException error){Status=error.Message;return;}
            long addedSurface=RetainsSurface(definition.Surface)?0:surfaceBytes;
            if((long)ResidentCells+cells>settings.MaximumResidentCells||(long)ResidentCandidates+candidates>settings.MaximumResidentCandidates||ResidentBytes+bytes+addedSurface>settings.MaximumResidentBytes)
            {Status="Scatter replacement deferred: active, pending and retired banks share the resident budgets";return;}
            var bank=new Bank{Definition=definition,Instance=owner.ScatterInstanceId,Profile=profile,Exclusions=exclusions,Cells=cells,Candidates=candidates,Bytes=bytes,SurfaceBytes=surfaceBytes};
            bank.Retirement=new Retirement{Bank=bank};
            bank.ProfileSources=new PlanetScatterSpecies[settings.Species.Length];bank.ProfileRules=new SurfaceScatterSpecies[settings.Species.Length];
            for(int i=0;i<settings.Species.Length;i++){bank.ProfileSources[i]=settings.Species[i];bank.ProfileRules[i]=settings.Species[i].Placement;}
            try
            {
                foreach(var item in desired)
                {
                    item.Gpu=new PlanetScatterGpu(definition,owner.ScatterMaterialsReady,owner.ScatterInstanceId,item.Rules,item.Cells,
                        item.Source.UnscaledBoundingRadius,state);bank.Species.Add(item);
                }
                pending=bank;Status="Canonical GPU scatter cells are generating";
            }
            catch(ArgumentException error){bank.Dispose();Status=error.Message;}
            catch(NotSupportedException error){bank.Dispose();Status=error.Message;}
            catch(InvalidOperationException error){bank.Dispose();Status=error.Message;}
            CountResidency();
        }
        void GeneratePending(int maximumCells)
        {
            if(pending.ReadinessRequested)return;
            if(generationFrame!=Time.frameCount){generationFrame=Time.frameCount;generationCells=0;}
            int remaining=maximumCells-generationCells;if(remaining<=0)return;maximumCells=remaining;
            var cmd=CommandBufferPool.Get("Canonical scatter placement cells");
            try
            {
                foreach(var item in pending.Species)
                {
                    int count=Math.Min(maximumCells,item.Cells.Length-item.GeneratedCells);if(count<=0)continue;
                    item.Gpu.GenerateCells(cmd,count);item.GeneratedCells+=count;generationCells+=count;maximumCells-=count;if(maximumCells==0)break;
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally{CommandBufferPool.Release(cmd);}
            foreach(var item in pending.Species)if(item.GeneratedCells<item.Cells.Length)return;
            foreach(var item in pending.Species)item.Gpu.RequestReadiness();pending.ReadinessRequested=true;
        }
        void PollPending()
        {
            if(pending==null||!pending.ReadinessRequested)return;
            foreach(var item in pending.Species)if(!item.Gpu.PollReadiness())return;
            foreach(var item in pending.Species)if(item.Gpu.PreparationStatus!=SurfaceSampleStatus.Ready)
            {Status=item.Gpu.Status;Retire(pending);pending=null;return;}
            // A second fixed-tick revision can arrive before the first new-bank
            // draw. Do not transfer an intermediate bank's unsubmitted history.
            if(active!=null&&active.HistoryPending){Status="Waiting for the previous revision's first native draw";return;}
            pending.Ready=true;
            PreparePendingHistory();
            if(stagedRevision.HasValue){Status="Canonical GPU scatter revision staged";return;}
            PublishPending();
        }
        void PublishPending()
        {
            if(active!=null)Retire(active,pending.HistoryPending?pending:null);
            active=pending;pending=null;active.PublishedTime=Time.realtimeSinceStartup;Status="Canonical GPU scatter ready";
        }
        void PreparePendingHistory()
        {
            if(pending.HistoryPrepared)return;
            if(active!=null&&active.Instance.Equals(pending.Instance)&&active.Profile.Equals(pending.Profile))
                foreach(var item in pending.Species)foreach(var old in active.Species)
                    if(old.Rules.SpeciesId==item.Rules.SpeciesId)
                    {item.Gpu.PrepareHistory(old.Gpu);item.HistorySource=old.Gpu;pending.HistoryPending=true;}
            pending.HistoryPrepared=true;
        }
        static bool SameRules(in SurfaceScatterSpecies a,in SurfaceScatterSpecies b)=>
            a.SpeciesId==b.SpeciesId&&a.Seed==b.Seed&&a.FixedLevel==b.FixedLevel&&a.CandidatesPerCell==b.CandidatesPerCell&&
            a.DensityPerSquareMetre==b.DensityPerSquareMetre&&a.MinimumHeight==b.MinimumHeight&&a.MaximumHeight==b.MaximumHeight&&
            a.MaximumSlopeDegrees==b.MaximumSlopeDegrees&&a.MinimumWetness==b.MinimumWetness&&a.MaximumWetness==b.MaximumWetness&&
            a.NormalSampleMetres==b.NormalSampleMetres&&a.MaterialAffinity.Equals(b.MaterialAffinity)&&a.ScaleRange.Equals(b.ScaleRange)&&
            a.RequiredChannels==b.RequiredChannels&&a.ExcludeSurfaceStamps==b.ExcludeSurfaceStamps&&a.MinimumReferenceChordSpacingMetres==b.MinimumReferenceChordSpacingMetres;
        static bool SamePlacement(PlanetScatterSettings settings,Bank bank)
        {
            if(bank==null||bank.ProfileRules.Length!=settings.Species.Length)return false;
            for(int i=0;i<bank.ProfileRules.Length;i++)
            {
                PlanetScatterSpecies source=null;
                foreach(var candidate in settings.Species)if(candidate.SpeciesId==bank.ProfileRules[i].SpeciesId){source=candidate;break;}
                if(source!=bank.ProfileSources[i]||!source||!SameRules(source.Placement,bank.ProfileRules[i]))return false;
            }
            foreach(var item in bank.Species)if(item.Source.UnscaledBoundingRadius!=item.Gpu.MeshRadius)return false;
            return true;
        }
        public bool PrepareRevision(PlanetDefinition candidate,SurfaceScatterExclusions proposedExclusions=null)
        {
            if(disposed||!candidate.IsValid||candidate.GeneratorVersion!=3)return false;
            if(stagedRevision.HasValue&&!SameSurface(stagedRevision.Value,candidate))CancelRevision();
            if(!stagedRevision.HasValue&&pending!=null){Retire(pending);pending=null;}
            stagedRevision=candidate;stagedExclusions=proposedExclusions;
            if(active!=null&&retired.Capacity==retired.Count)retired.Capacity=retired.Count+8;
            var settings=owner.ScatterSettings;
            if(!owner.Enabled||!settings||!settings.Enabled){stagedEmpty=true;return true;}
            if(!settings.IsValid||!owner.ScatterMaterialsReady||!SystemInfo.supportsAsyncGPUReadback){Status="Scatter revision settings/materials are not ready";return false;}
            var profile=settings.CapturePlacement().ContentDigest;var exclusions=proposedExclusions?.ContentDigest??default;
            if(pending!=null&&(!SameSurface(pending.Definition,candidate)||!pending.Profile.Equals(profile)||!pending.Exclusions.Equals(exclusions)||!SamePlacement(settings,pending)))
            {Retire(pending);pending=null;}
            var q=(double4)((quaternion)owner.PlanetRotation).value;
            var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),owner.CameraPosition-candidate.Center);
            var desired=new List<SpeciesBank>();
            if(!TryCollect(settings,candidate,localCamera,desired,out var reason)){stagedEmpty=false;Status=reason;return false;}
            if(desired.Count==0){if(pending!=null){Retire(pending);pending=null;}stagedEmpty=true;return true;}
            stagedEmpty=false;
            if(pending==null)
            {
                // Also refresh every existing active cell. A commit cannot retain a
                // rendered old-revision prop merely because interest moved meanwhile.
                if(active!=null&&active.Profile.Equals(profile))foreach(var item in desired)foreach(var old in active.Species)
                    if(item.Rules.SpeciesId==old.Rules.SpeciesId)
                    {var union=new HashSet<SurfaceTileKey>(item.Cells);foreach(var key in old.Cells)union.Add(key);var keys=new List<SurfaceTileKey>(union);keys.Sort();item.Cells=keys.ToArray();}
                TryBegin(settings,candidate,proposedExclusions,profile,exclusions,desired);
            }
            if(pending!=null){GeneratePending(settings.MaximumNewCellsPerFrame);PollPending();}
            return RevisionReady(candidate.Surface);
        }
        public bool RevisionReady(PlanetSurfaceDescriptor descriptor)
        {
            if(!stagedRevision.HasValue||!stagedRevision.Value.Surface.Equals(descriptor))return false;
            var settings=owner.ScatterSettings;
            if(active!=null&&retired.Count==retired.Capacity)return false;
            if(stagedEmpty)
            {
                if(!owner.Enabled||!settings||!settings.Enabled)return true;
                if(!settings.IsValid)return false;
                var q=(double4)((quaternion)owner.PlanetRotation).value;
                var localCamera=PlanetField.Rotate(new double4(-q.xyz,q.w),owner.CameraPosition-owner.Definition.Center);
                if(!math.all(math.isfinite(localCamera))||math.lengthsq(localCamera)<=0)return false;
                foreach(var source in settings.Species)
                    if(TryInterest(localCamera,stagedRevision.Value.Radius,stagedRevision.Value.Relief,
                        source.ShadowDistance+source.UnscaledBoundingRadius*source.ScaleRange.y,out _,out _))return false;
                return true;
            }
            return settings&&settings.Enabled&&settings.IsValid&&owner.ScatterMaterialsReady&&pending!=null&&pending.Ready&&
                pending.HistoryPrepared&&pending.Definition.Surface.Equals(descriptor)&&pending.Instance.Equals(owner.ScatterInstanceId)&&SamePlacement(settings,pending);
        }
        public bool CommitRevision(PlanetSurfaceDescriptor descriptor)
        {
            if(!RevisionReady(descriptor))return false;
            if(stagedEmpty){if(active!=null)Retire(active);active=null;}
            else PublishPending();
            stagedRevision=null;stagedExclusions=null;stagedEmpty=false;return true;
        }
        public void CancelRevision()
        {
            if(!stagedRevision.HasValue)return;
            if(pending!=null){Retire(pending);pending=null;}
            stagedRevision=null;stagedExclusions=null;stagedEmpty=false;
        }
        void Retire(Bank bank,Bank historyConsumer=null)
        {if(bank!=null){bank.Retirement.HistoryConsumer=historyConsumer;retired.Add(bank.Retirement);}}
        void EndFrame(ScriptableRenderContext context,Camera[] cameras)
        {
            foreach(var item in retired)
            {
                if(item.Requested)continue;item.Requested=true;
                if(item.HistoryConsumer!=null&&item.HistoryConsumer.HistoryPending){item.Requested=false;continue;}
                // Enqueued after native draws. This small asynchronous query is also
                // the retirement fence; buffers are never released while readers run.
                AsyncGPUReadback.Request(item.Bank.Species[0].Gpu.Counters,request=>
                {item.Complete=true;if(disposed)item.Bank.Dispose();});
            }
            CollectRetired();
        }
        void CollectRetired(){for(int i=retired.Count-1;i>=0;i--)if(retired[i].Complete){retired[i].Bank.Dispose();retired.RemoveAt(i);}CountResidency();}
        void CountResidency()
        {
            ResidentCells=0;ResidentCandidates=0;ResidentBytes=0;
            var surfaces=new HashSet<PlanetSurfaceDescriptor>();Add(active,surfaces);Add(pending,surfaces);foreach(var item in retired)Add(item.Bank,surfaces);
        }
        void Add(Bank bank,HashSet<PlanetSurfaceDescriptor> surfaces)
        {if(bank==null)return;ResidentCells+=bank.Cells;ResidentCandidates+=bank.Candidates;ResidentBytes+=bank.Bytes;if(surfaces.Add(bank.Definition.Surface))ResidentBytes+=bank.SurfaceBytes;}
        bool RetainsSurface(PlanetSurfaceDescriptor descriptor)
        {if(active!=null&&active.Definition.Surface.Equals(descriptor)||pending!=null&&pending.Definition.Surface.Equals(descriptor))return true;foreach(var item in retired)if(item.Bank.Definition.Surface.Equals(descriptor))return true;return false;}
        void ReleaseBanks()
        {if(active!=null){active.HistoryPending=false;Retire(active);}if(pending!=null){pending.HistoryPending=false;Retire(pending);}active=null;pending=null;CountResidency();}
        public void Dispose()
        {
            if(disposed)return;disposed=true;RenderPipelineManager.beginCameraRendering-=BeginCamera;RenderPipelineManager.endFrameRendering-=EndFrame;
            ReleaseBanks();EndFrame(default,Array.Empty<Camera>());
        }
    }
}
