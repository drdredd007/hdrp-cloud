using System.Collections.Generic;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class PlanetLodSelector
    {
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
            return result;
        }
    }
}
