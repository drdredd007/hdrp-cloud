using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Bounded editor picking against the generator field, independent of visual LOD.</summary>
    public static class PlanetSurfacePicking
    {
        public static bool TryViewportRay(double2 uv,double aspect,double verticalFov,double4 cameraRotation,out double3 direction)
        {
            direction=default;
            if(!math.all(math.isfinite(uv)) || math.any(uv<0) || math.any(uv>1) || !math.isfinite(aspect) || aspect<=0 ||
                !math.isfinite(verticalFov) || verticalFov<=0 || verticalFov>=179 ||
                !math.all(math.isfinite(cameraRotation)) || math.lengthsq(cameraRotation)<1e-20)return false;
            double tangent=math.tan(math.radians(verticalFov)*.5);
            direction=PlanetField.Rotate(math.normalize(cameraRotation),math.normalize(new double3((uv.x*2-1)*aspect*tangent,(uv.y*2-1)*tangent,1)));
            return true;
        }
        public static bool TryPick(PlanetDefinition definition,double4 planetRotation,double3 rayOrigin,double3 rayDirection,
            out PlanetSurfaceAddress address,out double distance)
        {
            address=default;distance=0;
            if(!definition.IsValid || !math.all(math.isfinite(planetRotation)) || math.lengthsq(planetRotation)<1e-20 ||
                !math.all(math.isfinite(rayOrigin)) || !math.all(math.isfinite(rayDirection)) || math.lengthsq(rayDirection)<1e-20)return false;
            var rotation=math.normalize(planetRotation);var inverse=new double4(-rotation.xyz,rotation.w);
            var origin=PlanetField.Rotate(inverse,rayOrigin-definition.Center);
            var direction=PlanetField.Rotate(inverse,math.normalize(rayDirection));
            double outer=definition.Radius+definition.Relief;
            double closest=-math.dot(origin,direction);
            if(!math.isfinite(closest) || closest<=0)return false;
            double miss=math.lengthsq(math.cross(origin,direction));
            if(!math.isfinite(miss) || miss>outer*outer)return false;
            double start=math.max(0,closest-math.sqrt(math.max(0,outer*outer-miss)));
            double end=miss<definition.Radius*definition.Radius?closest-math.sqrt(definition.Radius*definition.Radius-miss):closest;
            double Clearance(double t)
            {
                var point=origin+direction*t;double radius=math.length(point);
                return radius-definition.Radius-math.max(0,PlanetField.Height(definition,point/math.max(1e-20,radius)));
            }
            if(Clearance(0)<0 || end<start)return false;
            double previous=start;
            for(int sample=0;sample<=1024;sample++)
            {
                double current=math.lerp(start,end,sample/1024.0);
                if(Clearance(current)<=1e-7)
                {
                    double lo=previous,hi=current;
                    for(int step=0;step<40;step++)
                    {double mid=(lo+hi)*.5;if(Clearance(mid)>0)lo=mid;else hi=mid;}
                    distance=(lo+hi)*.5;var point=math.normalize(origin+direction*distance);
                    address=new PlanetSurfaceAddress {Latitude=math.degrees(math.asin(math.clamp(point.y,-1,1))),Longitude=math.degrees(math.atan2(point.z,point.x))};
                    return address.IsValid;
                }
                previous=current;
            }
            return false;
        }
    }
}
