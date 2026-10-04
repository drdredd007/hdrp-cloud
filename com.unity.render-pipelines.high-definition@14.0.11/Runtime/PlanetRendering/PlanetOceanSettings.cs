using System;
using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Opaque static sea appearance. The immutable recipe remains the only sea-level authority.</summary>
    [Serializable]
    public struct PlanetOceanSettings
    {
        public bool Enabled;
        [ColorUsage(false,true)] public Color Albedo;
        [Range(0,1)] public float Smoothness;
        public static PlanetOceanSettings Default=>new PlanetOceanSettings
        {Enabled=true,Albedo=new Color(.015f,.055f,.12f,1),Smoothness=.85f};
        // Old physical checkpoints do not serialize render settings. Their zero-filled
        // metadata has the same default appearance as a newly created generator.
        public PlanetOceanSettings Resolved=>!Enabled&&Albedo.r==0&&Albedo.g==0&&Albedo.b==0&&Albedo.a==0&&Smoothness==0?Default:this;
        public bool IsValid=>math.all(math.isfinite(new float4(Albedo.r,Albedo.g,Albedo.b,Albedo.a)))&&
            Albedo.r>=0&&Albedo.g>=0&&Albedo.b>=0&&Albedo.a>0&&math.isfinite(Smoothness)&&Smoothness>=0&&Smoothness<=1;
    }
    public static class PlanetOceanMath
    {
        public static bool TryRadius(SurfaceRecipe recipe,out double radius)
        {radius=recipe.Radius+recipe.SeaLevel;return recipe.IsValid&&recipe.Style==SurfaceStyle.EarthLike&&math.isfinite(radius)&&radius>0;}
        /// <summary>Outside-camera first surface hit. Stable near root avoids subtracting two planetary distances.</summary>
        public static bool TryIntersect(double3 cameraFromCenter,double3 direction,double radius,out double distance)
        {
            distance=0;
            if(!math.all(math.isfinite(cameraFromCenter))||!math.all(math.isfinite(direction))||!math.isfinite(radius)||radius<=0)return false;
            double length=math.length(cameraFromCenter),rayLength=math.length(direction);
            if(!math.isfinite(length)||length<=radius||!math.isfinite(rayLength)||rayLength<=0)return false;
            direction/=rayLength;double b=math.dot(cameraFromCenter,direction),height=length-radius;
            if(b>=0)return false;
            double c=height*(2*radius+height),discriminant=b*b-c;
            if(!math.isfinite(discriminant)||discriminant<0)return false;
            distance=c/(-b+Math.Sqrt(discriminant));
            return math.isfinite(distance)&&distance>0;
        }
    }
}
