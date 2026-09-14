using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class PlanetLodSelector
    {
        public const int MaximumSupportedLevel=10;
        public static bool Contains(PlanetPatchKey parent,PlanetPatchKey child)
            => parent.Face==child.Face && parent.Level<=child.Level && child.X>>(child.Level-parent.Level)==parent.X && child.Y>>(child.Level-parent.Level)==parent.Y;
        public static double Error(PlanetDefinition definition,PlanetPatchKey key,double3 camera,double pixelsPerRadian)
        {
            var direction=PlanetField.Direction(key,.5,.5);
            double span=math.PI/(2*(1<<key.Level));
            double distance=math.length(camera);
            // Conservative patch cone: keep horizon patches, leave hidden hemisphere coarse.
            if(distance>definition.Radius && math.dot(direction,camera/distance)+math.sin(math.min(math.PI/2,span))<definition.Radius/distance)return 0;
            double closest=math.max(1,math.distance(direction*definition.Radius,camera)-definition.Radius*span);
            double step=span/32;
            // Curvature term plus estimated relief variation; not a measured terrain error bound.
            double metres=definition.Radius*(1-math.cos(step))+definition.Relief*step*4;
            return metres*pixelsPerRadian/closest;
        }
        // PatchBudget bounds error-driven refinement; balancing can add a bounded number of leaves on top.
        public static List<PlanetPatchKey> Select(PlanetDefinition definition,double3 camera,int height,float fieldOfView,
            PlanetLodSettings requested,IReadOnlyList<PlanetPatchKey> previous=null)
        {
            var result=new List<PlanetPatchKey>();
            if(!definition.IsValid || !math.all(math.isfinite(camera)) || height<=0 || !math.isfinite(fieldOfView))return result;
            var settings=requested.Clamped;
            for(int face=0;face<6;face++)result.Add(new PlanetPatchKey(face,0,0,0));
            double pixelScale=height/(2*math.tan(math.radians(math.clamp(fieldOfView,1,179))*.5));
            while(result.Count+3<=settings.PatchBudget)
            {
                int best=-1;double score=1;
                for(int i=0;i<result.Count;i++)
                {
                    var key=result[i];if(key.Level>=settings.MaximumLevel)continue;
                    bool wasSplit=false;
                    if(previous!=null)for(int j=0;j<previous.Count;j++)if(previous[j].Level>key.Level && Contains(key,previous[j])){wasSplit=true;break;}
                    double error=Error(definition,key,camera,pixelScale)/(settings.PixelError*(wasSplit?.7:1.2));
                    if(error>score){best=i;score=error;}
                }
                if(best<0)break;
                var parent=result[best];result.RemoveAt(best);
                for(int y=0;y<2;y++)for(int x=0;x<2;x++)result.Add(new PlanetPatchKey(parent.Face,parent.Level+1,parent.X*2+x,parent.Y*2+y));
            }
            Balance(result);
            return result;
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
}
