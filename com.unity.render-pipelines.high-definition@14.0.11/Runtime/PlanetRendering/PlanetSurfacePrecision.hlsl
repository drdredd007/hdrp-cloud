#ifndef SPACERUNNER_SURFACE_PRECISION_INCLUDED
#define SPACERUNNER_SURFACE_PRECISION_INCLUDED
// SM5 has no double logarithm intrinsic. Reduce to an exact dyadic scale, then use
// log(m)=2*atanh((m-1)/(m+1)) in FP64 for 1<=m<2. Sixteen retained odd terms
// leave a series tail below 2e-17; a float logarithm only seeds the integer scale.
double SurfaceRegionalFilterLod(double ratio,int levels)
{
    if(ratio<=1||levels<=0)return 0;
    // Mip levels are bounded by positive Int32 source dimensions. Their powers
    // of two are exactly representable in float, including the early clamp.
    if(ratio>=(double)exp2((float)levels))return (double)levels;
    int exponent=(int)floor(log2((float)ratio));
    double mantissa=ratio/(double)exp2((float)exponent);
    if(mantissa<1){mantissa*=2;exponent--;}
    else if(mantissa>=2){mantissa*=.5;exponent++;}
    double y=(mantissa-1)/(mantissa+1),z=y*y,series=1.0L/33.0L;
    [unroll]for(int term=15;term>=1;term--)
        series=1.0L/(double)(2*term+1)+z*series;
    return (double)exponent+2.885390081777926814719849362L*y*(1+z*series);
}
#endif
