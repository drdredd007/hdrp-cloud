using System;

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public struct PlanetDefinition
    {
        public int Id,Seed,GeneratorVersion;

        public double Radius,Relief;
        public double3 Center;
        public static PlanetDefinition Prototype => new PlanetDefinition
        {Id=1,Seed=7243,GeneratorVersion=1,Radius=6371000.0/3,Relief=6000};
        public bool IsValid => Id>0 && GeneratorVersion==1 && Radius>0 && Relief>=0 && Relief<Radius*.1 &&
            math.isfinite(Radius) && math.isfinite(Relief) && math.all(math.isfinite(Center));
    }
    // Stable face/quadtree address: the same address and height field will be used by surface LOD.
    public struct PlanetPatchKey : IEquatable<PlanetPatchKey>
    {
        public int Face,Level,X,Y;
        public bool Equals(PlanetPatchKey other)=>Face==other.Face && Level==other.Level && X==other.X && Y==other.Y;
        public override bool Equals(object other)=>other is PlanetPatchKey key && Equals(key);
        public override int GetHashCode(){unchecked{return ((Face*397+Level)*397+X)*397+Y;}}
        public PlanetPatchKey(int face,int level,int x,int y){Face=face;Level=level;X=x;Y=y;}
    }
    public static class PlanetField
    {
        public const double FarScale=.001;
        public static double3 Rotate(double4 q,double3 point) => point+2*math.cross(q.xyz,math.cross(q.xyz,point)+q.w*point);
        public static double3 Direction(PlanetPatchKey key,double u,double v)
        {
            double count=1<<key.Level;
            double a=2*(key.X+u)/count-1,b=2*(key.Y+v)/count-1;
            double3 cube;
            switch(key.Face)
            {
                case 0:cube=new double3(1,b,-a);break;
                case 1:cube=new double3(-1,b,a);break;
                case 2:cube=new double3(a,1,-b);break;
                case 3:cube=new double3(a,-1,b);break;
                case 4:cube=new double3(a,b,1);break;
                default:cube=new double3(-a,b,-1);break;
            }
            return math.normalize(cube);
        }
        public static double Height(PlanetDefinition definition,double3 direction)
        {
            // Noise coordinates depend only on planet-local direction, seed and generator version.
            float3 p=(float3)direction;
            float3 shift=new float3(definition.Seed%101,definition.Seed%79,definition.Seed%67)*.137f;
            float continents=noise.snoise(p*2.7f+shift);
            float detail=.24f*noise.snoise(p*11+shift)+.07f*noise.snoise(p*39-shift);
            return math.clamp((continents+detail-.08f)*definition.Relief,-definition.Relief,definition.Relief);
        }
        public static double3 Surface(PlanetDefinition definition,double3 direction)
            => direction*(definition.Radius+math.max(0,Height(definition,direction)));
        public static float3 Normal(PlanetDefinition definition,double3 direction)
        {
            double3 tangent=math.normalize(math.cross(math.abs(direction.y)<.9?new double3(0,1,0):new double3(1,0,0),direction));
            double3 bitangent=math.cross(direction,tangent);
            const double step=.0001;
            var a=Surface(definition,math.normalize(direction+tangent*step))-Surface(definition,math.normalize(direction-tangent*step));
            var b=Surface(definition,math.normalize(direction+bitangent*step))-Surface(definition,math.normalize(direction-bitangent*step));
            return (float3)math.normalize(math.cross(a,b));
        }
        public static float4 Color(PlanetDefinition definition,double3 direction)
        {
            double height=Height(definition,direction);
            float polar=math.saturate(((float)math.abs(direction.y)-.9f)*15);
            float3 color;
            if(height<=0)color=math.lerp(new float3(.009f,.032f,.075f),new float3(.02f,.12f,.16f),math.saturate(1+(float)(height/math.max(1,definition.Relief))*3));
            else
            {
                float dryness=math.saturate(noise.snoise((float3)direction*8+definition.Seed*.01f)*.8f+.45f);
                color=math.lerp(new float3(.025f,.095f,.035f),new float3(.28f,.19f,.085f),dryness);
                color=math.lerp(color,new float3(.24f,.22f,.19f),math.saturate((float)(height/math.max(1,definition.Relief))*2));
            }
            return new float4(math.lerp(color,new float3(.72f,.79f,.82f),polar),1);
        }
        public static double3 RelativeScaled(double3 frameRelativeCenter,double3 camera,double3 patchCenter)
            => ((frameRelativeCenter-camera)+patchCenter)*FarScale;
    }
    [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
    public struct PlanetPatchJob : IJobParallelFor
    {
        public PlanetDefinition Definition;
        public PlanetPatchKey Key;
        public int Resolution;
        public double3 Pivot;
        public NativeArray<float3> Positions,Normals;
        public NativeArray<float4> Colors;
        public void Execute(int index)
        {
            double3 d=PlanetField.Direction(Key,(double)(index%(Resolution+1))/Resolution,(double)(index/(Resolution+1))/Resolution);
            Positions[index]=(float3)((PlanetField.Surface(Definition,d)-Pivot)*PlanetField.FarScale);
            Normals[index]=PlanetField.Normal(Definition,d);Colors[index]=PlanetField.Color(Definition,d);
        }
    }
}
