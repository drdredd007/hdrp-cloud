// World Orogen GPU numeric support. GPL-3.0-only; original source commit cc2662b4.
#ifndef WORLD_OROGEN_MATH_INCLUDED
#define WORLD_OROGEN_MATH_INCLUDED
static const double WorldPi=3.1415926535897932384626433832795L;
double WorldAbs(double x){return x<0?-x:x;}
double WorldMin(double a,double b){return a<b?a:b;}
double WorldMax(double a,double b){return a>b?a:b;}
double WorldClamp(double x,double a,double b){return WorldMin(b,WorldMax(a,x));}
double WorldFloor(double x){int n=(int)x;return (double)n-(x<(double)n?1:0);}
double WorldDot(double3 a,double3 b){return (a.x*b.x+a.y*b.y)+a.z*b.z;}
double3 WorldCross(double3 a,double3 b){return double3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);}
double WorldSqrt(double x){double y=0;if(!(x<=0)){y=(double)sqrt((float)x);[loop]for(int k=0;k<4;k++)y=(y+x/y)*0.5L;}return y;}
double3 WorldUnit(double3 x){double n=WorldSqrt(WorldDot(x,x));return n>0?x/n:double3(0,0,0);}
double WorldExp(double x)
{
    double result=0;
    if(!(x<=-745))
    {
        if(x>=709)result=asdouble(0u,0x7ff00000u);
        else
        {
            int k=(int)WorldFloor(x/0.693147180559945309417232121458L+0.5L);
            double r=x-k*0.693147180559945309417232121458L,sum=1,term=1;
            [loop]for(int j=1;j<=16;j++){term*=r/j;sum+=term;}
            result=k>=-1022?sum*asdouble(0u,(uint)(k+1023)<<20):(sum*asdouble(0u,1u<<20))*asdouble(0u,(uint)(k+2045)<<20);
        }
    }
    return result;
}
double WorldLog(double x)
{
    if(x<=0)return asdouble(0u,0xfff00000u);uint lo,hi;asuint(x,lo,hi);
    int e=(int)((hi>>20)&2047u)-1023;
    if(e==-1023){x*=4503599627370496.0L;asuint(x,lo,hi);e=(int)((hi>>20)&2047u)-1023-52;}
    double m=asdouble(lo,(hi&0xfffffu)|0x3ff00000u);
    if(m>1.4142135623730950488L){m*=0.5L;e++;}
    double z=(m-1)/(m+1),zz=z*z,term=z,sum=z;
    [loop]for(int j=1;j<20;j++){term*=zz;sum+=term/(2*j+1);}
    return 2*sum+e*0.693147180559945309417232121458L;
}
double WorldPow(double x,double y){if(x==0)return y==0?1:0;return WorldExp(y*WorldLog(x));}
double WorldSin(double x)
{
    x-=WorldFloor((x+WorldPi)/(2*WorldPi))*(2*WorldPi);
    if(x>WorldPi*.5L)x=WorldPi-x;else if(x<-WorldPi*.5L)x=-WorldPi-x;
    double term=x,sum=x,sq=x*x;[loop]for(int j=1;j<=9;j++){term*=-sq/((2*j)*(2*j+1));sum+=term;}return sum;
}
double WorldCos(double x){return WorldSin(x+WorldPi*.5L);}
double WorldAtanUnit(double x)
{
    bool transformed=x>0.4142135623730950488L;
    double z=transformed?(x-1)/(x+1):x,term=z,sum=z,zz=-z*z;
    [loop]for(int j=1;j<=24;j++){term*=zz;sum+=term/(2*j+1);}
    return sum+(transformed?WorldPi*.25L:0);
}
double WorldAtan(double x){double a=WorldAbs(x),v=a>1?WorldPi*.5L-WorldAtanUnit(1/a):WorldAtanUnit(a);return x<0?-v:v;}
double WorldAtan2(double y,double x){double result=0;if(x>0)result=WorldAtan(y/x);else if(x<0)result=WorldAtan(y/x)+(y<0?-WorldPi:WorldPi);else if(y!=0)result=y<0?-WorldPi*.5L:WorldPi*.5L;return result;}
double WorldAsin(double x){x=WorldClamp(x,-1,1);return WorldAtan2(x,WorldSqrt(WorldMax(0,(1-x)*(1+x))));}
double WorldAcos(double x){return WorldPi*.5L-WorldAsin(x);}
double WorldTanh(double x){double a=WorldAbs(x);double t=(1-WorldExp(-2*a))/(1+WorldExp(-2*a));return x<0?-t:t;}
double WorldSmooth(double x){x=WorldClamp(x,0,1);return x*x*(3-2*x);}
#endif
