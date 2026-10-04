// Adapted from World Orogen js/rng.js and js/simplex-noise.js.
// https://github.com/raguilar011095/planet_heightmap_generation
// Commit cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    public sealed class WorldOrogenRandom
    {
        long state;
        public WorldOrogenRandom(double seed) { state=(long)(Math.Abs(Math.Floor(seed*9301+49297))%2147483646)+1; }
        public double Next() { state=state*16807%2147483647; return (state-1)/2147483646.0; }
        public int NextInt(int count) { if(count<=0)throw new ArgumentOutOfRangeException(nameof(count));return (int)Math.Floor(Next()*count); }
    }

    /// <summary>Upstream double-precision reference and identical permutation capture for GPU kernels.</summary>
    public sealed class WorldOrogenNoise
    {
        public readonly uint[] Permutation=new uint[512];
        public static uint[] CreatePermutation(double seed)=>new WorldOrogenNoise(seed).Permutation;
        static readonly int3[] Gradients={new int3(1,1,0),new int3(-1,1,0),new int3(1,-1,0),new int3(-1,-1,0),
            new int3(1,0,1),new int3(-1,0,1),new int3(1,0,-1),new int3(-1,0,-1),new int3(0,1,1),new int3(0,-1,1),new int3(0,1,-1),new int3(0,-1,-1)};
        public WorldOrogenNoise(double seed=0)
        {
            var random=new WorldOrogenRandom(seed);var values=new uint[256];for(uint i=0;i<256;i++)values[i]=i;
            for(int i=255;i>0;i--){int j=random.NextInt(i+1);uint temporary=values[i];values[i]=values[j];values[j]=temporary;}
            for(int i=0;i<512;i++)Permutation[i]=values[i&255];
        }
        public double Noise(double x,double y,double z)
        {
            const double f=1.0/3,h=1.0/6;double s=(x+y+z)*f;
            int i=(int)Math.Floor(x+s),j=(int)Math.Floor(y+s),k=(int)Math.Floor(z+s);
            double t=(i+j+k)*h,x0=x-i+t,y0=y-j+t,z0=z-k+t;
            int3 first,second;
            if(x0>=y0){if(y0>=z0){first=new int3(1,0,0);second=new int3(1,1,0);}else if(x0>=z0){first=new int3(1,0,0);second=new int3(1,0,1);}else{first=new int3(0,0,1);second=new int3(1,0,1);}}
            else{if(y0<z0){first=new int3(0,0,1);second=new int3(0,1,1);}else if(x0<z0){first=new int3(0,1,0);second=new int3(0,1,1);}else{first=new int3(0,1,0);second=new int3(1,1,0);}}
            int ii=i&255,jj=j&255,kk=k&255;
            double n0=Corner(x0,y0,z0,ii,jj,kk);
            double n1=Corner(x0-first.x+h,y0-first.y+h,z0-first.z+h,ii+first.x,jj+first.y,kk+first.z);
            double n2=Corner(x0-second.x+2*h,y0-second.y+2*h,z0-second.z+2*h,ii+second.x,jj+second.y,kk+second.z);
            double n3=Corner(x0-1+3*h,y0-1+3*h,z0-1+3*h,ii+1,jj+1,kk+1);
            return 32*(n0+n1+n2+n3);
        }
        double Corner(double x,double y,double z,int i,int j,int k)
        {
            double a=.6-x*x-y*y-z*z;if(a<=0)return 0;a*=a;
            var g=Gradients[Permutation[i+Permutation[j+Permutation[k]]]%12];return a*a*(g.x*x+g.y*y+g.z*z);
        }
        public double Fbm(double x,double y,double z,int octaves=5,double persistence=2.0/3)
        {double sum=0,max=0,amp=1;for(int o=0;o<octaves;o++){int f=1<<o;sum+=amp*Noise(x*f,y*f,z*f);max+=amp;amp*=persistence;}return sum/max;}
        public double RidgedFbm(double x,double y,double z,int octaves=6,double lacunarity=2,double gain=.5,double offset=1)
        {double sum=0,freq=1,amp=1,previous=1,max=0;for(int o=0;o<octaves;o++){double n=offset-Math.Abs(Noise(x*freq,y*freq,z*freq));n*=n;sum+=n*amp*previous;max+=amp;previous=Math.Min(n,1);freq*=lacunarity;amp*=gain;}return sum/max;}
    }
}
