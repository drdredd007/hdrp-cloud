#ifndef SPACERUNNER_STRUCTURAL_FIELD_INCLUDED
#define SPACERUNNER_STRUCTURAL_FIELD_INCLUDED
// Explicit persisted authority, never regenerated from a seed in a shader. GPU buffers mirror the leased CPU field.
struct SurfaceStructureProvince { uint4 centerXY,centerZ,buoyancy; int4 flags; };
struct SurfaceStructureEdge { uint4 startXY,startZ,endXY,endZ,normalXY,normalZ,convergence; int4 flags; };
#if !defined(PLANET_SURFACE_STRUCTURE_LANDFORM_ONLY)
StructuredBuffer<SurfaceStructureProvince> _SurfaceStructureProvinces;
StructuredBuffer<SurfaceStructureEdge> _SurfaceStructureEdges;
StructuredBuffer<int2> _SurfaceStructureBins;
StructuredBuffer<int> _SurfaceStructureReferences;
#endif
StructuredBuffer<float> _SurfaceStructureRawMacro;
int4 _SurfaceStructureCounts;
uint4 _SurfaceStructureBounds,_SurfaceStructureSeaShelf,_SurfaceStructureBeltFeature,_SurfaceStructureCoastMountain;
double StructureSmooth(double x) { x=SurfaceClamp(x,0,1); return x*x*(3-2*x); }
double StructureExpMinus(double x)
{
    if(x>=64)return 0;
    double y=SurfaceMax(0,x)/64,v=1.0L/479001600.0L;
    v=v*-y+1.0L/39916800.0L;v=v*-y+1.0L/3628800.0L;v=v*-y+1.0L/362880.0L;
    v=v*-y+1.0L/40320.0L;v=v*-y+1.0L/5040.0L;v=v*-y+1.0L/720.0L;v=v*-y+1.0L/120.0L;
    v=v*-y+1.0L/24.0L;v=v*-y+1.0L/6.0L;v=v*-y+0.5L;v=v*-y+1;v=v*-y+1;
    [loop]for(int expSquareIndex=0;expSquareIndex<6;expSquareIndex++)v*=v;return v;
}
double StructureFloor(double x) { int integral=(int)x; return (double)integral-(x<(double)integral?1:0); }
double StructureSin(double x)
{
    const double pi=3.1415926535897932384626433832795L;
    x-=StructureFloor((x+pi)/(2*pi))*(2*pi);
    if(x>pi*0.5)x=pi-x;else if(x<-pi*0.5)x=-pi-x;
    double square=x*x,sum=x,term=x;
    [loop]for(int sinTermIndex=1;sinTermIndex<=8;sinTermIndex++){term*=-square/((double)(2*sinTermIndex)*(double)(2*sinTermIndex+1));sum+=term;}return sum;
}
// Keep sequential terms in a bounded loop: native normals and automatic masks sample this field repeatedly.
// Expanding every term at each call site makes the shader optimizer's work grow independently of GPU work.
double StructureAsinLow(double x)
{
    double sum=x,term=x,square=x*x;
    [loop]for(int structureAsinTermIndex=1;structureAsinTermIndex<=32;structureAsinTermIndex++){double odd=(double)(2*structureAsinTermIndex-1);term*=square*odd*odd/((double)(2*structureAsinTermIndex)*(double)(2*structureAsinTermIndex+1));sum+=term;}
    return sum;
}
double StructureAsin(double x)
{
    x=SurfaceClamp(x,0,1);
    return x<=0.5?StructureAsinLow(x):asdouble(0x54442d18u,0x3ff921fbu)-2*StructureAsinLow(SurfaceSqrt((1-x)*0.5));
}
double StructureArc(double3 a,double3 b) { double3 delta=a-b; return 2*StructureAsin(SurfaceMin(1,SurfaceSqrt(SurfaceDot(delta,delta))*0.5)); }
double StructureEnvelope(double x) { return x>=3?0:StructureExpMinus(x*x)*(1-StructureSmooth(x-2)); }
double StructureRandom(int edge,int slot,uint stream)
{
    uint x=(uint)(_SurfaceStructureCounts.w^(edge*7907))^((uint)(slot^(slot<0?-1:0))*0x9e3779b9u)^(stream*0x85ebca6bu);
    x^=x>>16u;x*=0x7feb352du;x^=x>>15u;x*=0x846ca68bu;x^=x>>16u;return ((double)x+0.5)/4294967296.0L;
}
double StructureBranchRoot(int edge,int slot,double across,double scale)
{
    const double pi=3.1415926535897932384626433832795L;
    double phase=StructureRandom(edge,slot,19u)*2*pi;
    return ((double)slot+0.2L+0.6L*StructureRandom(edge,slot,17u))*scale+0.35L*across+scale*0.12L*(StructureSin(across/(scale*2)+phase)-StructureSin(phase));
}
double StructureDistance(SurfaceStructureEdge edge,double3 d,double radius,out double along,out double across)
{
    double3 start=SurfaceD3(edge.startXY,edge.startZ),end=SurfaceD3(edge.endXY,edge.endZ),normal=SurfaceD3(edge.normalXY,edge.normalZ);
    double signedDistance=SurfaceDot(d,normal);double3 projected=d-normal*signedDistance;
    if(SurfaceDot(projected,projected)<1e-24L)projected=start;else projected=SurfaceUnit(projected);
    if(SurfaceDot(projected,start+end)<0)projected=-projected;
    bool inside=SurfaceDot(SurfaceCross(start,projected),normal)>=-1e-13L&&SurfaceDot(SurfaceCross(projected,end),normal)>=-1e-13L;
    double3 closest=inside?projected:(SurfaceDot(d,start)>=SurfaceDot(d,end)?start:end);
    along=StructureArc(start,closest)*radius;across=(signedDistance<0?-1:1)*StructureAsin(SurfaceMin(1,SurfaceAbs(signedDistance)))*radius;
    return StructureArc(d,closest)*radius;
}
double StructureBandWeight(double wavelength,double footprint)
{
    double support=footprint>0?footprint:1.0L;
    if(footprint<=0)return 1;
    return StructureSmooth((wavelength/support-2)*0.5);
}
#if !defined(PLANET_SURFACE_STRUCTURE_LEGACY_ONLY) && !defined(PLANET_SURFACE_STRUCTURE_LANDFORM_ONLY)
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceDrainage.hlsl"
#endif
#if !defined(PLANET_SURFACE_STRUCTURE_LEGACY_ONLY) && !defined(PLANET_SURFACE_STRUCTURE_DRAINAGE_ONLY)
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/PlanetRendering/PlanetSurfaceLandformFilter.hlsl"
#endif
#if !defined(PLANET_SURFACE_STRUCTURE_LEGACY_ONLY) && !defined(PLANET_SURFACE_STRUCTURE_LANDFORM_ONLY)
bool StructureBaseFour(double3 input,int face,double2 uv,out double macro)
{
    macro=0;double3 d=SurfaceUnit(input);double coast;
    if(!SurfaceDrainageCoast(d,coast))return false;
    double radius=SurfaceD(_SurfaceRadiusAndNormal.xy),shelf=SurfaceD(_SurfaceStructureSeaShelf.zw),belt=SurfaceD(_SurfaceStructureBeltFeature.xy);
    int bx=(int)SurfaceMin(31,uv.x*32),by=(int)SurfaceMin(31,uv.y*32);int2 range=_SurfaceStructureBins[face*1024+by*32+bx];
    double uplift=0,rift=0;
    [loop]for(int j=0;j<range.y;j++)
    {
        SurfaceStructureEdge edge=_SurfaceStructureEdges[_SurfaceStructureReferences[range.x+j]];
        double along,across,distance=StructureDistance(edge,d,radius,along,across),convergence=SurfaceD(edge.convergence.xy);
        if(convergence>0.08L)uplift=SurfaceMax(uplift,StructureEnvelope(distance/belt)*(convergence-0.08L)/0.92L);
        if(convergence<-0.08L)rift=SurfaceMax(rift,StructureEnvelope(distance/belt)*-convergence);
    }
    double minimum=SurfaceD(_SurfaceStructureBounds.xy),maximum=SurfaceD(_SurfaceStructureBounds.zw),sea=SurfaceClamp(SurfaceD(_SurfaceStructureSeaShelf.xy),minimum,maximum);
    double land=maximum-sea,ocean=sea-minimum,mountain=SurfaceMin(1,SurfaceD(_SurfaceStructureCoastMountain.zw)/0.3L);
    if(coast>=0)
    {
        double weightedBuoyancy=0,weightSum=0;
        [loop]for(int provinceIndex=0;provinceIndex<_SurfaceStructureCounts.z;provinceIndex++)
        {
            SurfaceStructureProvince province=_SurfaceStructureProvinces[provinceIndex];
            double weight=StructureExpMinus(12*(1-SurfaceDot(d,SurfaceD3(province.centerXY,province.centerZ))));
            double buoyancy=SurfaceClamp((SurfaceD(province.buoyancy.xy)-0.12L)/0.15L,0,1);
            weightedBuoyancy+=weight*buoyancy;weightSum+=weight;
        }
        if(weightSum<=0)return false;
        double lowland=land*(0.02L+0.10L*weightedBuoyancy/weightSum);
        macro=sea+lowland*StructureSmooth(coast/(shelf*2))+land*0.78L*mountain*uplift*StructureSmooth(coast/(shelf*0.4L));
    }
    else macro=sea-ocean*(0.06L*StructureSmooth(-coast/shelf)+0.84L*StructureSmooth(-coast/(shelf*4)))+ocean*0.18L*rift*StructureSmooth(-coast/(shelf*4));
    macro=SurfaceClamp(macro,minimum,maximum);return SurfaceFinite(macro);
}
#endif
double SurfaceStructureBand(double3 d,int face,double2 uv,double footprint)
{
    if(_SurfaceStructureCounts.x==0)return 0;
#if !defined(PLANET_SURFACE_STRUCTURE_LEGACY_ONLY) && !defined(PLANET_SURFACE_STRUCTURE_DRAINAGE_ONLY)
    if(_SurfaceStructureCounts.x==3)
    {
        // The approximation retains the captured authority and its baked residual.
        // Its correction must not be faded a second time by the old shelf/belt weights.
        double macro=0;if(!SurfaceLandformFilteredHeight(d,footprint,macro))return asdouble(0u,0x7ff80000u);
        double raw=SurfaceBilinear(_SurfaceStructureRawMacro,face*(_SurfaceStructureCounts.y+1)*(_SurfaceStructureCounts.y+1),int2(_SurfaceStructureCounts.y,_SurfaceStructureCounts.y),uv);
        return macro-raw;
    }
#endif
#if !defined(PLANET_SURFACE_STRUCTURE_LANDFORM_ONLY)
    double radius=SurfaceD(_SurfaceRadiusAndNormal.xy),shelf=SurfaceD(_SurfaceStructureSeaShelf.zw),belt=SurfaceD(_SurfaceStructureBeltFeature.xy),scale=SurfaceD(_SurfaceStructureBeltFeature.zw);
    double macroWeight=StructureBandWeight(SurfaceMin(shelf,belt),footprint),ridgeWeight=StructureBandWeight(scale*0.2L,footprint);
#if !defined(PLANET_SURFACE_STRUCTURE_LEGACY_ONLY)
    if(_SurfaceStructureCounts.x==2)
    {
        if(macroWeight==0)return 0;
        double macro;if(!StructureBaseFour(d,face,uv,macro))return asdouble(0u,0x7ff80000u);
        double raw=SurfaceBilinear(_SurfaceStructureRawMacro,face*(_SurfaceStructureCounts.y+1)*(_SurfaceStructureCounts.y+1),int2(_SurfaceStructureCounts.y,_SurfaceStructureCounts.y),uv);
        return (macro-raw)*macroWeight;
    }
#endif
    if(macroWeight==0&&ridgeWeight==0)return 0;
    double coastThreshold=SurfaceD(_SurfaceStructureCoastMountain.xy),mountain=SurfaceMin(1,SurfaceD(_SurfaceStructureCoastMountain.zw)/0.3L);
    double minimum=SurfaceD(_SurfaceStructureBounds.xy),maximum=SurfaceD(_SurfaceStructureBounds.zw),sea=SurfaceClamp(SurfaceD(_SurfaceStructureSeaShelf.xy),minimum,maximum);
    int plate=0;double best=-2;
    [loop]for(int provinceIndex=0;provinceIndex<_SurfaceStructureCounts.z;provinceIndex++){double score=SurfaceDot(d,SurfaceD3(_SurfaceStructureProvinces[provinceIndex].centerXY,_SurfaceStructureProvinces[provinceIndex].centerZ));if(score>best){best=score;plate=provinceIndex;}}
    double basin=0,basinWeight=0;
    [loop]for(int basinIndex=0;basinIndex<_SurfaceStructureCounts.z;basinIndex++)
    {
        SurfaceStructureProvince province=_SurfaceStructureProvinces[basinIndex];double w=StructureExpMinus(SurfaceMax(0,(best-SurfaceDot(d,SurfaceD3(province.centerXY,province.centerZ)))*radius/shelf));
        basin+=w*SurfaceD(province.buoyancy.xy);basinWeight+=w;
    }
    int bx=(int)SurfaceMin(31,uv.x*32),by=(int)SurfaceMin(31,uv.y*32);int2 range=_SurfaceStructureBins[face*1024+by*32+bx];
    double coast=shelf*4+SurfaceAbs(coastThreshold),uplift=0,rift=0,sum=0,weightSum=0,landRelief=maximum-sea,oceanRelief=sea-minimum;
    [loop]for(int edgeReferenceIndex=0;edgeReferenceIndex<range.y;edgeReferenceIndex++)
    {
        int edgeIndex=_SurfaceStructureReferences[range.x+edgeReferenceIndex];SurfaceStructureEdge edge=_SurfaceStructureEdges[edgeIndex];
        double along=0,across=0,distance=StructureDistance(edge,d,radius,along,across),convergence=SurfaceD(edge.convergence.xy);
        bool continentalA=edge.flags.x!=0,continentalB=edge.flags.y!=0;
        if(continentalA!=continentalB)coast=SurfaceMin(coast,distance);
        if(convergence<-0.08L&&!continentalA&&!continentalB)rift=SurfaceMax(rift,StructureEnvelope(distance/belt)*-convergence);
        if(convergence<=0.08L||(!continentalA&&!continentalB))continue;
        double strength=(convergence-0.08L)/0.92L;
        uplift=SurfaceMax(uplift,StructureEnvelope(distance/belt)*strength*(continentalA&&continentalB?1:0.75L));
        if(ridgeWeight==0)continue;
        double weight=StructureEnvelope(distance/(belt*1.8L))*strength;if(weight==0)continue;
        int slot=(int)StructureFloor((along-SurfaceAbs(across)*0.35L)/scale);double crest=0,tributary=0;
        [loop]for(int branchOffset=-1;branchOffset<=1;branchOffset++)
        {
            int branch=slot+branchOffset;double root=StructureBranchRoot(edgeIndex,branch,SurfaceAbs(across),scale),next=StructureBranchRoot(edgeIndex,branch+1,SurfaceAbs(across),scale);
            double width=scale*(0.12L+0.08L*StructureRandom(edgeIndex,branch,23u)),q=(along-root)/width;
            crest=SurfaceMax(crest,StructureExpMinus(q*q)*(0.65L+0.35L*StructureRandom(edgeIndex,branch,25u)));
            q=(along-(root+next)*0.5)/(scale*0.1L);tributary=SurfaceMax(tributary,StructureExpMinus(q*q));
        }
        double downstream=0.45L+0.55L*StructureSmooth(SurfaceAbs(across)/(scale*3)),z=(SurfaceAbs(across)-scale*3)/(scale*0.18L),trunk=StructureExpMinus(z*z);
        double foothill=1-StructureSmooth((SurfaceAbs(across)-scale*3)/scale);
        sum+=weight*landRelief*0.1L*mountain*(0.75L*crest*foothill-0.55L*tributary*downstream*foothill-0.6L*trunk);weightSum+=weight;
    }
    double ridges=sum/SurfaceMax(1,weightSum);coast=coast*(_SurfaceStructureProvinces[plate].flags.x!=0?1:-1)-coastThreshold;basin/=basinWeight;double macro=0;
    if(coast>=0)macro=sea+landRelief*basin*StructureSmooth(coast/shelf)+landRelief*0.72L*mountain*uplift*StructureSmooth(coast/(shelf*0.25L));
    else { double shore=StructureSmooth(-coast/shelf),deep=StructureSmooth(-coast/(shelf*4));macro=sea-oceanRelief*(0.06L*shore+(0.65L+basin)*deep)+oceanRelief*0.22L*rift*deep; }
    macro=SurfaceClamp(macro,minimum,maximum);
    double raw=SurfaceBilinear(_SurfaceStructureRawMacro,face*(_SurfaceStructureCounts.y+1)*(_SurfaceStructureCounts.y+1),int2(_SurfaceStructureCounts.y,_SurfaceStructureCounts.y),uv);
    return (macro-raw)*macroWeight+ridges*ridgeWeight;
#else
    // A mismatched authority must fail, never silently sample a different family.
    return asdouble(0u,0x7ff80000u);
#endif
}
#endif
