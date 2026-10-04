// World Orogen js/simplex-noise.js; cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
#ifndef WORLD_OROGEN_NOISE_INCLUDED
#define WORLD_OROGEN_NOISE_INCLUDED
StructuredBuffer<uint> _WorldNoisePermutation;
static const int3 WorldOrogenGradients[12]={int3(1,1,0),int3(-1,1,0),int3(1,-1,0),int3(-1,-1,0),int3(1,0,1),int3(-1,0,1),int3(1,0,-1),int3(-1,0,-1),int3(0,1,1),int3(0,-1,1),int3(0,1,-1),int3(0,-1,-1)};
double WorldOrogenNoiseCorner(double3 p,int3 cell)
{
    double a=.6L-p.x*p.x-p.y*p.y-p.z*p.z,result=0;
    if(!(a<=0)){a*=a;uint gradient=_WorldNoisePermutation[cell.x+_WorldNoisePermutation[cell.y+_WorldNoisePermutation[cell.z]]]%12u;
    int3 g=WorldOrogenGradients[gradient];result=a*a*(g.x*p.x+g.y*p.y+g.z*p.z);}return result;
}
double WorldOrogenNoise3D(double3 p)
{
    const double f=1.0L/3.0L,h=1.0L/6.0L;double s=(p.x+p.y+p.z)*f;
    // SM5 floor(double) lowers through float on some backends; truncation correction remains FP64.
    double3 v=p+s;int3 c=(int3)v;c-=int3(v.x<c.x?1:0,v.y<c.y?1:0,v.z<c.z?1:0);
    double t=(c.x+c.y+c.z)*h;double3 p0=p-(double3)c+t;int3 a,b;
    if(p0.x>=p0.y){if(p0.y>=p0.z){a=int3(1,0,0);b=int3(1,1,0);}else if(p0.x>=p0.z){a=int3(1,0,0);b=int3(1,0,1);}else{a=int3(0,0,1);b=int3(1,0,1);}}
    else{if(p0.y<p0.z){a=int3(0,0,1);b=int3(0,1,1);}else if(p0.x<p0.z){a=int3(0,1,0);b=int3(0,1,1);}else{a=int3(0,1,0);b=int3(1,1,0);}}
    int3 baseCell=c&255;
    double n0=WorldOrogenNoiseCorner(p0,baseCell);
    double n1=WorldOrogenNoiseCorner(p0-(double3)a+h,baseCell+a);
    double n2=WorldOrogenNoiseCorner(p0-(double3)b+2*h,baseCell+b);
    double n3=WorldOrogenNoiseCorner(p0-1+3*h,baseCell+1);return 32*(n0+n1+n2+n3);
}
double WorldOrogenFbm(double3 p,int octaves=5,double persistence=2.0L/3.0L)
{double sum=0,total=0,amplitude=1;[loop]for(int octave=0;octave<octaves;octave++){int frequency=1<<octave;sum+=amplitude*WorldOrogenNoise3D(p*frequency);total+=amplitude;amplitude*=persistence;}return sum/total;}
double WorldOrogenRidgedFbm(double3 p,int octaves=6,double lacunarity=2,double gain=.5L,double offset=1)
{double sum=0,frequency=1,amplitude=1,previous=1,total=0;[loop]for(int octave=0;octave<octaves;octave++){double n=offset-abs(WorldOrogenNoise3D(p*frequency));n*=n;sum+=n*amplitude*previous;total+=amplitude;previous=min(n,1.0L);frequency*=lacunarity;amplitude*=gain;}return sum/total;}
#endif
