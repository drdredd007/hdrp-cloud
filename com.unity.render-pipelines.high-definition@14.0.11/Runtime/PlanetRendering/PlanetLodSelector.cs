using System.Collections.Generic;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class PlanetLodSelector
    {
        public const int MaximumSupportedLevel=16;
        /// <summary>Exact float parameter sent to the existing far generator, without changing its geometry.</summary>
        public static double RenderedSkirtDepthMetres(PlanetDefinition definition,PlanetPatchKey key,int resolution=32)
        {
            double span=math.PI/(2*(1<<key.Level));
            return (float)math.max(10,definition.Relief*.1+definition.Radius*(1-math.cos(span/resolution))*4);
        }
        /// <summary>Conservative roundoff of patch-local float position packing, parent interpolation and skirt subtraction.
        /// The relative cube displacement is at most 2*sqrt(2)*R/count; no planetary centre is rounded here.</summary>
        public static double RenderedPositionReserveMetres(PlanetDefinition definition,PlanetPatchKey key,double minimumHeight,double maximumHeight)
        {
            double magnitude=math.max(math.abs(minimumHeight),math.abs(maximumHeight));
            return 16*1.7320508075688772935*1.1920928955078125e-7*
                (2*System.Math.Sqrt(2)*definition.Radius/(1L<<key.Level)+magnitude+RenderedSkirtDepthMetres(definition,key)+1);
        }
        /// <summary>Distance to a conservative spherical cone and radial interval containing the whole patch.
        /// Cube-face vectors have length at least one. Normalisation bounds their chord by the unnormalised
        /// centre-to-corner distance sqrt(2)/count, independently of face position. This bound does not rely
        /// on finite terrain probes and remains valid for signed terrain between R-Relief and R+Relief.</summary>
        public static double DistanceLowerBound(PlanetDefinition definition,PlanetPatchKey key,double3 camera)
            =>DistanceToRadialCone(definition,key,camera,math.max(0,definition.Radius-definition.Relief),definition.Radius+definition.Relief);
        /// <summary>Uses a retained worker certificate enclosing fine/parent vertices and stitched triangle interiors.
        /// Invalid or non-positive shells retain the global definition envelope.</summary>
        public static double DistanceLowerBound(PlanetDefinition definition,PlanetPatchKey key,double3 camera,
            double minimumRenderedHeight,double maximumRenderedHeight)
        {
            if(!math.isfinite(minimumRenderedHeight)||!math.isfinite(maximumRenderedHeight)||minimumRenderedHeight>maximumRenderedHeight||
                !(definition.Radius+minimumRenderedHeight>0))return DistanceLowerBound(definition,key,camera);
            return DistanceToRadialCone(definition,key,camera,definition.Radius+minimumRenderedHeight,definition.Radius+maximumRenderedHeight);
        }
        /// <summary>Distance to the radial cone plus a metric packing ball. Float error has transverse
        /// components too; widening only the radial interval cannot enclose those components.</summary>
        public static double DistanceLowerBound(PlanetDefinition definition,PlanetPatchKey key,double3 camera,
            double minimumRenderedHeight,double maximumRenderedHeight,double positionReserveMetres)
        {
            if(!math.isfinite(positionReserveMetres)||positionReserveMetres<0)return 0;
            return math.max(0,DistanceLowerBound(definition,key,camera,minimumRenderedHeight,maximumRenderedHeight)-positionReserveMetres);
        }
        static double DistanceToRadialCone(PlanetDefinition definition,PlanetPatchKey key,double3 camera,double radialMin,double radialMax)
        {
            if(!definition.IsValid || key.Face<0 || key.Face>=6 || key.Level<0 || key.Level>MaximumSupportedLevel ||
                key.X<0 || key.Y<0 || key.X>=(1<<key.Level) || key.Y>=(1<<key.Level) || !math.all(math.isfinite(camera)))
                return 0;
            double distance=math.length(camera);
            if(!math.isfinite(distance))return 0;
            if(distance==0)return radialMin;
            var direction=PlanetField.Direction(key,.5,.5);
            // atan2 avoids cancellation from acos(dot) for distant planets and small angular gaps.
            double angle=math.atan2(math.length(math.cross(direction,camera/distance)),math.dot(direction,camera/distance));
            double coneAngle=2*math.asin(math.min(1,System.Math.Sqrt(2)/(2*(1L<<key.Level))));
            double gap=math.max(0,angle-coneAngle),radial=math.clamp(distance*math.cos(gap),radialMin,radialMax);
            double sine=math.sin(gap*.5);
            return math.sqrt(math.max(0,(distance-radial)*(distance-radial)+4*distance*radial*sine*sine));
        }
        public static bool Contains(PlanetPatchKey parent,PlanetPatchKey child)
            => parent.Face==child.Face && parent.Level<=child.Level && child.X>>(child.Level-parent.Level)==parent.X && child.Y>>(child.Level-parent.Level)==parent.Y;
        public static double Error(PlanetDefinition definition,PlanetPatchKey key,double3 camera,double pixelsPerRadian)
        {
            PlanetSurfaceLodContext context=null;
            if(definition.GeneratorVersion==3)PlanetSurfaceDataRegistry.TryAcquireLodContext(definition.Surface,out context);
            try{return Error(definition,key,camera,pixelsPerRadian,null,context,out _,out _,out _);}finally{context?.Dispose();}
        }
        public static double Error(PlanetDefinition definition,PlanetPatchKey key,double3 camera,double pixelsPerRadian,PlanetLodView view)
        {
            if(!view.IsValid)return double.PositiveInfinity;
            if(!IntersectsRenderEnvelope(definition,key,camera,view))return 0;
            return Error(definition,key,camera,pixelsPerRadian);
        }
        static bool IntersectsRenderEnvelope(PlanetDefinition definition,PlanetPatchKey key,double3 camera,PlanetLodView view)
        {
            if(definition.GeneratorVersion!=3)return view.Intersects(definition,key,camera);
            double reserve=RenderedPositionReserveMetres(definition,key,-definition.Relief,definition.Relief);
            return view.Intersects(definition,key,camera,-definition.Relief-RenderedSkirtDepthMetres(definition,key)-reserve,
                definition.Relief+reserve,reserve);
        }
        static double GlobalRenderedDistanceLowerBound(PlanetDefinition definition,PlanetPatchKey key,double3 camera)
        {
            double reserve=RenderedPositionReserveMetres(definition,key,-definition.Relief,definition.Relief);
            return DistanceLowerBound(definition,key,camera,-definition.Relief-RenderedSkirtDepthMetres(definition,key)-reserve,
                definition.Relief+reserve,reserve);
        }
        static double Error(PlanetDefinition definition,PlanetPatchKey key,double3 camera,double pixelsPerRadian,
            PlanetLodView? view,PlanetSurfaceLodContext context,out bool complete,out bool pending,out bool bounded)
        {
            complete=true;pending=false;bounded=true;
            if(view.HasValue&&!IntersectsRenderEnvelope(definition,key,camera,view.Value))return 0;
            var direction=PlanetField.Direction(key,.5,.5);
            double span=System.Math.PI/(2*(1<<key.Level));
            double distance=math.length(camera);
            // Conservative patch cone: keep horizon patches, leave hidden hemisphere coarse.
            double outer=definition.GeneratorVersion==3?definition.Radius+definition.Relief:definition.Radius;
            double inner=definition.GeneratorVersion==3?math.max(0,definition.Radius-definition.Relief):definition.Radius;
            double horizonExtra=definition.GeneratorVersion==3?math.acos(math.clamp(inner/outer,0,1)):0;
            if(distance>outer&&math.dot(direction,camera/distance)+math.sin(math.min(System.Math.PI/2,span+horizonExtra))<inner/distance)return 0;
            double closest=definition.GeneratorVersion==3?math.max(1,GlobalRenderedDistanceLowerBound(definition,key,camera)):
                math.max(1,math.distance(direction*definition.Radius,camera)-outer*span);
            if(definition.GeneratorVersion==3)
            {
                if(context==null){complete=false;bounded=false;return double.PositiveInfinity;}
                var footprint=new SurfaceSamplingFootprint(2*(definition.Radius+definition.Relief)/((1<<key.Level)*32));
                var measured=context.Error(new SurfaceTileKey(key.Face,key.Level,key.X,key.Y),32,footprint);
                complete=measured.IsComplete;pending=measured.Status==SurfaceErrorStatus.Pending;bounded=measured.HasConservativeBound;
                if(bounded&&measured.HasRenderedHeightRange)
                {
                    double reserve=RenderedPositionReserveMetres(definition,key,measured.MinimumRenderedHeightMetres,measured.MaximumRenderedHeightMetres);
                    double minimum=measured.MinimumRenderedHeightMetres-RenderedSkirtDepthMetres(definition,key)-reserve;
                    double maximum=measured.MaximumRenderedHeightMetres+reserve;
                    if(view.HasValue&&!view.Value.Intersects(definition,key,camera,minimum,maximum,reserve))return 0;
                    closest=math.max(1,DistanceLowerBound(definition,key,camera,minimum,maximum,reserve));
                }
                return measured.TotalMetres*pixelsPerRadian/closest;
            }
            double step=span/32;
            // Curvature term plus estimated relief variation; not a measured terrain error bound.
            double metres=definition.Radius*(1-math.cos(step))+definition.Relief*step*4;
            return metres*pixelsPerRadian/closest;
        }
        // PatchBudget bounds error-driven refinement; balancing can add a bounded number of leaves on top.
        public static List<PlanetPatchKey> Select(PlanetDefinition definition,double3 camera,int height,float fieldOfView,
            PlanetLodSettings requested,IReadOnlyList<PlanetPatchKey> previous=null)
            =>Select(definition,camera,height,fieldOfView,requested,out _,previous);
        public static List<PlanetPatchKey> Select(PlanetDefinition definition,double3 camera,int height,float fieldOfView,
            PlanetLodSettings requested,out PlanetLodDiagnostics diagnostics,IReadOnlyList<PlanetPatchKey> previous=null)
            =>Select(definition,camera,height,fieldOfView,null,requested,out diagnostics,previous);
        public static List<PlanetPatchKey> Select(PlanetDefinition definition,double3 camera,int height,PlanetLodView view,
            PlanetLodSettings requested,out PlanetLodDiagnostics diagnostics,IReadOnlyList<PlanetPatchKey> previous=null)
            =>Select(definition,camera,height,view.FieldOfView,view,requested,out diagnostics,previous);
        public static List<PlanetPatchKey> Select(PlanetDefinition definition,double3 camera,int height,PlanetLodView view,
            PlanetLodSettings requested,IReadOnlyList<PlanetPatchKey> previous=null)
            =>Select(definition,camera,height,view,requested,out _,previous);
        static List<PlanetPatchKey> Select(PlanetDefinition definition,double3 camera,int height,float fieldOfView,PlanetLodView? view,
            PlanetLodSettings requested,out PlanetLodDiagnostics diagnostics,IReadOnlyList<PlanetPatchKey> previous)
        {
            var result=new List<PlanetPatchKey>();
            diagnostics=default;
            if(!definition.IsValid || !math.all(math.isfinite(camera)) || height<=0 || !math.isfinite(fieldOfView)||
                view.HasValue&&!view.Value.IsValid)return result;
            PlanetSurfaceLodContext context=null;
            diagnostics.UsesMeasuredTerrain=definition.GeneratorVersion==3;
            if(diagnostics.UsesMeasuredTerrain)
                diagnostics.SurfaceUnavailable=!PlanetSurfaceDataRegistry.TryAcquireLodContext(definition.Surface,out context);
            try
            {
            var settings=requested.Clamped;
            for(int face=0;face<6;face++)result.Add(new PlanetPatchKey(face,0,0,0));
            var blocked=new HashSet<PlanetPatchKey>();
            double pixelScale=height/(2*math.tan(math.radians(math.clamp(fieldOfView,1,179))*.5));
            while(result.Count+3<=settings.PatchBudget)
            {
                int best=-1;double score=1;
                for(int i=0;i<result.Count;i++)
                {
                    var key=result[i];if(key.Level>=settings.MaximumLevel||blocked.Contains(key))continue;
                    bool wasSplit=false;
                    if(previous!=null)for(int j=0;j<previous.Count;j++)if(previous[j].Level>key.Level && Contains(key,previous[j])){wasSplit=true;break;}
                    double error=Error(definition,key,camera,pixelScale,view,context,out bool complete,out bool pending,out bool bounded)/(settings.PixelError*(wasSplit?.7:1.2));
                    diagnostics.MeasurementIncomplete|=!complete;
                    diagnostics.PreparationPending|=pending;
                    if(!complete&&!bounded)continue; // Valid incomplete intervals refine under the same hard balanced leaf budget.
                    if(error>score){best=i;score=error;}
                }
                if(best<0)break;
                if(definition.GeneratorVersion==3)
                {
                    var key=result[best];
                    if(!TrySplitBalanced(result,best,settings.PatchBudget,out var balanced,out int extra))
                    {blocked.Add(key);diagnostics.PatchBudgetExceeded=true;continue;}
                    result=balanced;diagnostics.BalancingAddedPatches+=extra;continue;
                }
                var parent=result[best];result.RemoveAt(best);
                for(int y=0;y<2;y++)for(int x=0;x<2;x++)result.Add(new PlanetPatchKey(parent.Face,parent.Level+1,parent.X*2+x,parent.Y*2+y));
            }
            if(definition.GeneratorVersion!=3)
            {int beforeBalance=result.Count;Balance(result);diagnostics.BalancingAddedPatches=result.Count-beforeBalance;}
            diagnostics.PixelErrorSatisfied=true;
            diagnostics.MeasurementIncomplete=false;diagnostics.PreparationPending=false;
            foreach(var key in result)
            {
                double error=Error(definition,key,camera,pixelScale,view,context,out bool complete,out bool pending,out bool bounded);
                diagnostics.MeasurementIncomplete|=!complete;if(!complete&&bounded)diagnostics.ConservativeFallbackPatches++;diagnostics.MaximumPixelError=math.max(diagnostics.MaximumPixelError,error);
                diagnostics.PreparationPending|=pending;
                if(!complete)diagnostics.PixelErrorSatisfied=false;
                if(error<=settings.PixelError)continue;
                diagnostics.PixelErrorSatisfied=false;diagnostics.UnresolvedPatches++;
                diagnostics.MaximumLevelExceeded|=key.Level>=settings.MaximumLevel;
                diagnostics.PatchBudgetExceeded|=result.Count+3>settings.PatchBudget&&key.Level<settings.MaximumLevel;
            }
            return result;
            }
            finally{context?.Dispose();}
        }

        // Starting cover is balanced. Only newly created children can require a coarser neighbour to split,
        // so inspect that closure instead of scanning the entire cover for each proposed refinement.
        static bool TrySplitBalanced(List<PlanetPatchKey> current,int index,int budget,out List<PlanetPatchKey> result,out int extra)
        {
            result=null;extra=0;var set=new HashSet<PlanetPatchKey>(current);var queue=new Queue<PlanetPatchKey>();
            Split(current[index],set,queue);
            while(queue.Count>0)
            {
                if(set.Count>budget)return false;
                var key=queue.Dequeue();if(!set.Contains(key))continue;
                for(int edge=0;edge<4;edge++)
                {
                    if(!TryFindLeaf(set,OutsideEdge(key,edge),out var neighbour)||neighbour.Level>=key.Level-1)continue;
                    Split(neighbour,set,queue);queue.Enqueue(key);break;
                }
            }
            if(set.Count>budget)return false;
            extra=set.Count-current.Count-3;result=new List<PlanetPatchKey>(set);
            result.Sort((a,b)=>a.Face!=b.Face?a.Face-b.Face:a.Level!=b.Level?a.Level-b.Level:a.Y!=b.Y?a.Y-b.Y:a.X-b.X);return true;
        }
        static void Split(PlanetPatchKey parent,HashSet<PlanetPatchKey> set,Queue<PlanetPatchKey> queue)
        {
            set.Remove(parent);
            for(int y=0;y<2;y++)for(int x=0;x<2;x++)
            {var child=new PlanetPatchKey(parent.Face,parent.Level+1,parent.X*2+x,parent.Y*2+y);set.Add(child);queue.Enqueue(child);}
        }

        // Point just outside the middle of an edge. Edges: 0 v=0, 1 u=1, 2 v=1, 3 u=0 (PlanetPatchMesh skirt order).
        public static double3 OutsideEdge(PlanetPatchKey key,int edge)
        {
            const double outside=1e-3;
            switch(edge)
            {
                case 0:return PlanetField.Direction(key,.5,-outside);
                case 1:return PlanetField.Direction(key,1+outside,.5);
                case 2:return PlanetField.Direction(key,.5,1+outside);
                default:return PlanetField.Direction(key,-outside,.5);
            }
        }
        public static bool TryFindLeaf(HashSet<PlanetPatchKey> leaves,double3 direction,out PlanetPatchKey leaf)
        {
            for(int level=MaximumSupportedLevel;level>=0;level--)
            {
                leaf=PlanetField.Locate(direction,level);
                if(leaves.Contains(leaf))return true;
            }
            leaf=default;return false;
        }

        // Restricts a complete cover so edge-adjacent leaves differ by at most one level. Quadtree cells
        // align along cube-face seams, so a coarser neighbour always spans the whole edge and the edge
        // midpoint identifies it.
        public static void Balance(List<PlanetPatchKey> leaves)
        {
            var set=new HashSet<PlanetPatchKey>(leaves);
            var queue=new Queue<PlanetPatchKey>(leaves);
            while(queue.Count>0)
            {
                var key=queue.Dequeue();
                if(!set.Contains(key))continue;
                for(int edge=0;edge<4;edge++)
                {
                    if(!TryFindLeaf(set,OutsideEdge(key,edge),out var neighbour) || neighbour.Level>=key.Level-1)continue;
                    set.Remove(neighbour);
                    for(int y=0;y<2;y++)for(int x=0;x<2;x++)
                    {
                        var child=new PlanetPatchKey(neighbour.Face,neighbour.Level+1,neighbour.X*2+x,neighbour.Y*2+y);
                        set.Add(child);queue.Enqueue(child);
                    }
                    queue.Enqueue(key);break;
                }
            }
            if(set.Count==leaves.Count)return;
            leaves.Clear();leaves.AddRange(set);
            leaves.Sort((a,b)=>a.Face!=b.Face?a.Face-b.Face:a.Level!=b.Level?a.Level-b.Level:a.Y!=b.Y?a.Y-b.Y:a.X-b.X);
        }

        // Bit e is set when the neighbour across edge e is coarser; those edges are stitched to its vertices.
        public static int CoarserEdges(HashSet<PlanetPatchKey> leaves,PlanetPatchKey key)
        {
            int mask=0;
            for(int edge=0;edge<4;edge++)
                if(TryFindLeaf(leaves,OutsideEdge(key,edge),out var neighbour) && neighbour.Level<key.Level)mask|=1<<edge;
            return mask;
        }
    }
    public struct PlanetLodDiagnostics
    {
        public bool UsesMeasuredTerrain,SurfaceUnavailable,MeasurementIncomplete,PixelErrorSatisfied;
        public bool PreparationPending;
        public bool MaximumLevelExceeded,PatchBudgetExceeded;
        public int UnresolvedPatches,BalancingAddedPatches,ConservativeFallbackPatches;
        public double MaximumPixelError;
    }
}
