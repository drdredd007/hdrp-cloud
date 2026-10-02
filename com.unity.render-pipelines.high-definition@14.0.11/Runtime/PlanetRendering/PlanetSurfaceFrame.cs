using System;
using Unity.Mathematics;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public struct PlanetSurfaceAddress
    {
        public double Latitude,Longitude,Height,Heading;
        public bool AlignToTerrain;
        public bool IsValid=>math.isfinite(Latitude) && math.abs(Latitude)<=90 && math.isfinite(Longitude) && math.isfinite(Height) && math.isfinite(Heading);
    }
    public struct PlanetSurfaceFrame
    {
        // Planet-local double frame: +Y is up, +Z follows heading, +X is right.
        public double3 Position,Right,Up,Forward;
        public double4 Rotation;
        public bool IsValid
        {
            get
            {
                if(!math.all(math.isfinite(Position))||!math.all(math.isfinite(Right))||!math.all(math.isfinite(Up))||
                    !math.all(math.isfinite(Forward))||!math.all(math.isfinite(Rotation)))return false;
                if(math.abs(math.lengthsq(Right)-1)>1e-8||math.abs(math.lengthsq(Up)-1)>1e-8||
                    math.abs(math.lengthsq(Forward)-1)>1e-8||math.abs(math.lengthsq(Rotation)-1)>1e-8||
                    math.lengthsq(math.cross(Right,Up)-Forward)>1e-16)return false;
                return math.lengthsq(Rotate(new double3(1,0,0))-Right)<1e-16&&
                    math.lengthsq(Rotate(new double3(0,1,0))-Up)<1e-16&&math.lengthsq(Rotate(new double3(0,0,1))-Forward)<1e-16;
            }
        }
        double3 Rotate(double3 v)=>v+2*math.cross(Rotation.xyz,math.cross(Rotation.xyz,v)+Rotation.w*v);
        public double3 ToPlanet(double3 local)=>Position+Right*local.x+Up*local.y+Forward*local.z;
        public double3 ToLocal(double3 planet)
        {
            var delta=planet-Position;
            return new double3(math.dot(delta,Right),math.dot(delta,Up),math.dot(delta,Forward));
        }
    }
    public static class PlanetSurfaceCoordinates
    {
        // Longitude zero is +X; positive longitude turns toward +Z; north is +Y.
        public static double3 Direction(double latitude,double longitude)
        {
            double lat=math.radians(latitude),lon=math.radians(longitude%360);
            return new double3(math.cos(lat)*math.cos(lon),math.sin(lat),math.cos(lat)*math.sin(lon));
        }
        public static bool TryResolve(PlanetDefinition definition,PlanetSurfaceAddress address,out PlanetSurfaceFrame frame)
        {
            frame=default;if(!definition.IsValid || !address.IsValid)return false;
            if(definition.GeneratorVersion==3)
            {
                if(!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out var lease))return false;
                using(lease)return TryResolve(definition,address,lease.View,out frame);
            }
            var radial=Direction(address.Latitude,address.Longitude);
            double longitude=math.radians(address.Longitude%360);
            var east=new double3(-math.sin(longitude),0,math.cos(longitude));
            var north=math.normalize(math.cross(east,radial));
            var up=address.AlignToTerrain?math.normalize((double3)PlanetField.Normal(definition,radial)):radial;
            var forward=math.normalizesafe(north-up*math.dot(north,up),north);
            var right=math.normalize(math.cross(up,forward));forward=math.cross(right,up);
            double angle=math.radians(address.Heading%360),s=math.sin(angle),c=math.cos(angle);
            frame=new PlanetSurfaceFrame {Position=PlanetField.Surface(definition,radial)+radial*address.Height,
                Right=right*c-forward*s,Up=up,Forward=forward*c+right*s};
            frame.Rotation=Rotation(frame.Right,frame.Up,frame.Forward);
            return math.all(math.isfinite(frame.Position)) && math.dot(frame.Position,radial)>0 && math.all(math.isfinite(frame.Rotation));
        }
        public static bool TryResolve(PlanetDefinition definition,PlanetSurfaceAddress address,in NativeSurfaceView view,out PlanetSurfaceFrame frame)
        {
            frame=default;if(!definition.IsValid || !address.IsValid)return false;
            if(definition.GeneratorVersion!=3)return TryResolve(definition,address,out frame);
            var radial=Direction(address.Latitude,address.Longitude);
            if(PlanetSurfaceData.TrySurface(definition,view,radial,out var position)!=SurfaceSampleStatus.Ready)return false;
            var up=radial;
            if(address.AlignToTerrain)
            {
                if(PlanetSurfaceData.TryNormal(definition,view,radial,out var normal)!=SurfaceSampleStatus.Ready)return false;
                up=math.normalize((double3)normal);
            }
            double longitude=math.radians(address.Longitude%360);
            var east=new double3(-math.sin(longitude),0,math.cos(longitude));var north=math.normalize(math.cross(east,radial));
            var forward=math.normalizesafe(north-up*math.dot(north,up),north);var right=math.normalize(math.cross(up,forward));forward=math.cross(right,up);
            double angle=math.radians(address.Heading%360),s=math.sin(angle),c=math.cos(angle);
            frame=new PlanetSurfaceFrame {Position=position+radial*address.Height,Right=right*c-forward*s,Up=up,Forward=forward*c+right*s};
            frame.Rotation=Rotation(frame.Right,frame.Up,frame.Forward);
            return math.all(math.isfinite(frame.Position)) && math.dot(frame.Position,radial)>0 && math.all(math.isfinite(frame.Rotation));
        }
        static double4 Rotation(double3 x,double3 y,double3 z)
        {
            double trace=x.x+y.y+z.z;double4 q;
            if(trace>0)
            {
                double s=math.sqrt(trace+1)*2;
                q=new double4((y.z-z.y)/s,(z.x-x.z)/s,(x.y-y.x)/s,.25*s);
            }
            else if(x.x>y.y && x.x>z.z)
            {
                double s=math.sqrt(1+x.x-y.y-z.z)*2;
                q=new double4(.25*s,(y.x+x.y)/s,(z.x+x.z)/s,(y.z-z.y)/s);
            }
            else if(y.y>z.z)
            {
                double s=math.sqrt(1+y.y-x.x-z.z)*2;
                q=new double4((y.x+x.y)/s,.25*s,(z.y+y.z)/s,(z.x-x.z)/s);
            }
            else
            {
                double s=math.sqrt(1+z.z-x.x-y.y)*2;
                q=new double4((z.x+x.z)/s,(z.y+y.z)/s,.25*s,(x.y-y.x)/s);
            }
            return math.normalize(q);
        }
    }
}
