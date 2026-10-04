// Additional independent upstream SimplexNoise permutations; GPL-3.0-only, cc2662b4.
#ifndef WORLD_OROGEN_AUX_NOISE_INCLUDED
#define WORLD_OROGEN_AUX_NOISE_INCLUDED
#include "WorldOrogenNoise.hlsl"
double WorldAuxCorner(uint gradient,double3 p)
{double a=.6L-p.x*p.x-p.y*p.y-p.z*p.z,result=0;if(!(a<=0)){a*=a;int3 g=WorldOrogenGradients[gradient];result=a*a*(g.x*p.x+g.y*p.y+g.z*p.z);}return result;}
double WorldAuxNoise(StructuredBuffer<uint> permutation,double3 p)
{
    double s=(p.x+p.y+p.z)*(1.0L/3.0L);int3 ijk=(int3)(p+s);ijk-=int3(p.x+s<ijk.x?1:0,p.y+s<ijk.y?1:0,p.z+s<ijk.z?1:0);
    double t=(double)(ijk.x+ijk.y+ijk.z)*(1.0L/6.0L);double3 a=p-(double3)ijk+t;int3 b,c;
    if(a.x>=a.y){if(a.y>=a.z){b=int3(1,0,0);c=int3(1,1,0);}else if(a.x>=a.z){b=int3(1,0,0);c=int3(1,0,1);}else{b=int3(0,0,1);c=int3(1,0,1);}}
    else {if(a.y<a.z){b=int3(0,0,1);c=int3(0,1,1);}else if(a.x<a.z){b=int3(0,1,0);c=int3(0,1,1);}else{b=int3(0,1,0);c=int3(1,1,0);}}
    uint i=(uint)ijk.x&255u,j=(uint)ijk.y&255u,k=(uint)ijk.z&255u;
    uint h0=permutation[i+permutation[j+permutation[k]]]%12u;
    uint h1=permutation[i+b.x+permutation[j+b.y+permutation[k+b.z]]]%12u;
    uint h2=permutation[i+c.x+permutation[j+c.y+permutation[k+c.z]]]%12u;
    uint h3=permutation[i+1+permutation[j+1+permutation[k+1]]]%12u;
    return 32*(WorldAuxCorner(h0,a)+WorldAuxCorner(h1,a-b+1.0L/6.0L)+WorldAuxCorner(h2,a-c+2*(1.0L/6.0L))+WorldAuxCorner(h3,a-1+3*(1.0L/6.0L)));
}
double WorldAuxFbm(StructuredBuffer<uint> permutation,double x,double y,double z,int octaves=5,double persistence=2.0L/3.0L)
{
    double total=0,amplitude=1,frequency=1,normalization=0;
    [loop]for(int octave=0;octave<octaves;octave++){total+=amplitude*WorldAuxNoise(permutation,double3(x*frequency,y*frequency,z*frequency));normalization+=amplitude;amplitude*=persistence;frequency*=2;}
    return total/normalization;
}
double WorldAuxRidged(StructuredBuffer<uint> permutation,double x,double y,double z,int octaves=6,double lacunarity=2,double gain=.5L,double offset=1)
{
    double sum=0,amplitude=1,frequency=1,previous=1,total=0;
    [loop]for(int octave=0;octave<octaves;octave++){double n=offset-WorldAbs(WorldAuxNoise(permutation,double3(x*frequency,y*frequency,z*frequency)));n*=n;sum+=n*amplitude*previous;total+=amplitude;previous=WorldMin(n,1);frequency*=lacunarity;amplitude*=gain;}
    return sum/total;
}
#endif
