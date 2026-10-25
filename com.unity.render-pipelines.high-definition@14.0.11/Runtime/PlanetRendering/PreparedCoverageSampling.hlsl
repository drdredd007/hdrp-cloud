#ifndef PREPARED_COVERAGE_INCLUDED
#define PREPARED_COVERAGE_INCLUDED
StructuredBuffer<float4> _PreparedCoverageBank,_PreparedCoverageMacro,_PreparedCoverageParameters;
StructuredBuffer<float> _PreparedCoverageFoundation;
bool PCCoverage(){return _PreparedCoverageParameters[0].x>0;}
int PCWrap(int i,int n){return (i%n+n)%n;}
float4 PCRoles(float bits){uint b=(uint)bits;float3 rgb=float3(b&255,(b>>8)&255,(b>>16)&255);return float4(rgb,max(0,255-dot(rgb,1)))/255.0;}
double3 PCBankLevel(double u,double v,int bankIndex,int level,out double2 gradient,out float4 roles)
{
 int n=(int)(bankIndex==2?_PreparedCoverageParameters[4].y:bankIndex==1?_PreparedCoverageParameters[0].y:_PreparedCoverageParameters[0].x),first=bankIndex==2?(int)_PreparedCoverageParameters[4].z:bankIndex==1?(int)_PreparedCoverageParameters[4].x:0;
 [loop]for(int m=0;m<level;m++){first+=n*n;n=max(1,n/2);}
 double px=(u-PPFloor(u))*n-.5L,py=((1-v)-PPFloor(1-v))*n-.5L;int x=(int)PPFloor(px),y=(int)PPFloor(py);double tx=px-x,ty=py-y;
 float4 a=_PreparedCoverageBank[first+PCWrap(y,n)*n+PCWrap(x,n)],b=_PreparedCoverageBank[first+PCWrap(y,n)*n+PCWrap(x+1,n)];
 float4 c=_PreparedCoverageBank[first+PCWrap(y+1,n)*n+PCWrap(x,n)],e=_PreparedCoverageBank[first+PCWrap(y+1,n)*n+PCWrap(x+1,n)];
 gradient=double2(((double)b.x-a.x)*(1-ty)+((double)e.x-c.x)*ty,-(((double)c.x-a.x)*(1-tx)+((double)e.x-b.x)*tx))*n;
 roles=lerp(lerp(PCRoles(a.w),PCRoles(b.w),(float)tx),lerp(PCRoles(c.w),PCRoles(e.w),(float)tx),(float)ty);
 return ((double3)a.xyz*(1-tx)+(double3)b.xyz*tx)*(1-ty)+((double3)c.xyz*(1-tx)+(double3)e.xyz*tx)*ty;
}
double3 PCBank(double u,double v,int bankIndex,double footprint,out double2 gradient,out float4 roles){
 double3 value=PCBankLevel(u,v,bankIndex,0,gradient,roles);
 float side=bankIndex==2?_PreparedCoverageParameters[4].y:bankIndex==1?_PreparedCoverageParameters[0].y:_PreparedCoverageParameters[0].x;
 float lod=clamp(log2(max(1,(float)footprint*side/_PreparedCoverageParameters[1].x)),0,log2(side));
 if(lod>0){int l=(int)lod;double2 ignored;float4 a,b;double3 x=PCBankLevel(u,v,bankIndex,l,ignored,a),y=PCBankLevel(u,v,bankIndex,min(l+1,(int)log2(side)),ignored,b);value.yz=x.yz*(1-frac(lod))+y.yz*frac(lod);roles=lerp(a,b,frac(lod));}
 return value;
}
double PCBankSphere(double3 d,int bankIndex,double frequency,double3 shift,double footprint,out double3 gradient,out double3 normal,out float4 roles)
{
 double radius=_PreparedPlanetParameters[0].x,period=_PreparedCoverageParameters[1].x;
 double3 p=d*(radius/period)*frequency+shift,w=d*d;w*=w;w*=w;w*=w;w/=w.x+w.y+w.z;
 double2 gx,gy,gz;float4 rx,ry,rz;double3 x=PCBank(p.z,p.y,bankIndex,footprint*frequency,gx,rx),y=PCBank(p.x,p.z,bankIndex,footprint*frequency,gy,ry),z=PCBank(p.x,p.y,bankIndex,footprint*frequency,gz,rz);
 double height=w.x*x.x+w.y*y.x+w.z*z.x;
 double3 safe=double3(PPAbs(d.x)>1e-12L?d.x:1,PPAbs(d.y)>1e-12L?d.y:1,PPAbs(d.z)>1e-12L?d.z:1);
 double3 blend=16*w*(double3(x.x,y.x,z.x)-height)/safe/radius;
 gradient=(w.x*double3(0,gx.y,gx.x)+w.y*double3(gy.x,0,gy.y)+w.z*double3(gz.x,gz.y,0))*(frequency/period)+blend;
 normal=(w.x*double3(0,x.z,x.y)+w.y*double3(y.y,0,y.z)+w.z*double3(z.y,z.z,0))*frequency+blend;
 roles=(float)w.x*rx+(float)w.y*ry+(float)w.z*rz;return height;
}
double2 PCRegion(double3 d,out double3 gh,out double3 gw)
{
 double3 a=double3(PPAbs(d.x),PPAbs(d.y),PPAbs(d.z)),U,V,N;int face;
 if(a.x>=a.y&&a.x>=a.z){face=d.x>=0?0:1;U=double3(0,0,d.x>=0?-1:1);V=double3(0,1,0);N=double3(d.x>=0?1:-1,0,0);}
 else if(a.y>=a.z){face=d.y>=0?2:3;U=double3(1,0,0);V=double3(0,0,d.y>=0?-1:1);N=double3(0,d.y>=0?1:-1,0);}
 else{face=d.z>=0?4:5;U=double3(d.z>=0?1:-1,0,0);V=double3(0,1,0);N=double3(0,0,d.z>=0?1:-1);}
 int n=(int)_PreparedCoverageParameters[0].z;double den=PPDot(d,N),u=PPDot(d,U)/den,v=PPDot(d,V)/den;
 double px=(u*.5L+.5L)*(n-1),py=(v*.5L+.5L)*(n-1);int x=clamp((int)px,0,n-2),y=clamp((int)py,0,n-2),i=face*n*n+y*n+x;double tx=px-x,ty=py-y;
 double2 A=(double2)_PreparedCoverageMacro[i].xy,B=(double2)_PreparedCoverageMacro[i+1].xy,C=(double2)_PreparedCoverageMacro[i+n].xy,E=(double2)_PreparedCoverageMacro[i+n+1].xy;
 double2 du=((B-A)*(1-ty)+(E-C)*ty)*(.5L*(n-1)),dv=((C-A)*(1-tx)+(E-B)*tx)*(.5L*(n-1));
 double3 ju=(U-u*N)/(den*_PreparedPlanetParameters[0].x),jv=(V-v*N)/(den*_PreparedPlanetParameters[0].x);gh=du.x*ju+dv.x*jv;gw=du.y*ju+dv.y*jv;
 return (A*(1-tx)+B*tx)*(1-ty)+(C*(1-tx)+E*tx)*ty;
}
void PCRegionNormal(double3 d,out double3 gh,out double3 gw)
{
 double3 a=double3(PPAbs(d.x),PPAbs(d.y),PPAbs(d.z));int face;double u,v;
 if(a.x>=a.y&&a.x>=a.z){face=d.x>=0?0:1;u=(d.x>=0?-d.z:d.z)/a.x;v=d.y/a.x;}
 else if(a.y>=a.z){face=d.y>=0?2:3;u=d.x/a.y;v=(d.y>=0?-d.z:d.z)/a.y;}
 else{face=d.z>=0?4:5;u=(d.z>=0?d.x:-d.x)/a.z;v=d.y/a.z;}
 int n=(int)_PreparedCoverageParameters[0].z;double px=(u*.5L+.5L)*(n-1),py=(v*.5L+.5L)*(n-1);int x=clamp((int)px,0,n-2),y=clamp((int)py,0,n-2),i=face*n*n+y*n+x;double tx=px-x,ty=py-y;
 // Macro samples, height gradients, then weight gradients share one SRV.
 i+=6*n*n;
 gh=((double3)_PreparedCoverageMacro[i].xyz*(1-tx)+(double3)_PreparedCoverageMacro[i+1].xyz*tx)*(1-ty)+((double3)_PreparedCoverageMacro[i+n].xyz*(1-tx)+(double3)_PreparedCoverageMacro[i+n+1].xyz*tx)*ty;
 i+=6*n*n;
 gw=((double3)_PreparedCoverageMacro[i].xyz*(1-tx)+(double3)_PreparedCoverageMacro[i+1].xyz*tx)*(1-ty)+((double3)_PreparedCoverageMacro[i+n].xyz*(1-tx)+(double3)_PreparedCoverageMacro[i+n+1].xyz*tx)*ty;
}
void PCReference(double3 d,out double h,out double2 g)
{
 int nx=(int)_PreparedCoverageParameters[0].w,ny=nx/2,w=nx+1;double horizontal=PPSqrt(d.x*d.x+d.z*d.z),radius=_PreparedPlanetParameters[0].x;
 double u=PPAbs(d.y)>1-1e-15L?0:PPAtan2(d.z,d.x)/(2*PPPi)+.5L;u-=PPFloor(u);
 double px=u*nx,py=PPClamp((PPAtan2(d.y,horizontal)/PPPi+.5L)*ny,0,(double)ny);int x=min(nx-1,(int)px),y=min(ny-1,(int)py);double tx=px-x,ty=py-y;int at=y*w+x;
 double a=_PreparedCoverageFoundation[at],b=_PreparedCoverageFoundation[at+1],c=_PreparedCoverageFoundation[at+w],e=_PreparedCoverageFoundation[at+w+1];
 h=(a*(1-tx)+b*tx)*(1-ty)+(c*(1-tx)+e*tx)*ty;g=double2(horizontal>1e-15L?((b-a)*(1-ty)+(e-c)*ty)*nx/(2*PPPi*radius*horizontal):0,((c-a)*(1-tx)+(e-b)*tx)*ny/(PPPi*radius));
}
PreparedPlanetSurface PCSurface(double3 d,double baseHeight,double2 baseGradient,double footprint)
{
 double3 g,ng,pg,pn,rg,wg,rng,wng;float4 roles,pr;
 double h=PCBankSphere(d,0,1,0,footprint,g,ng,roles),fraction=_PreparedCoverageParameters[1].z;
 if(fraction>0){double3 dg,dn;float4 ignored;h+=fraction*PCBankSphere(d,0,_PreparedCoverageParameters[1].y,double3(.173L,.391L,.719L),footprint,dg,dn,ignored);g+=fraction*dg;ng+=fraction*dn;}
 double plain=PCBankSphere(d,1,1,0,footprint,pg,pn,pr);double2 region=PCRegion(d,rg,wg);PCRegionNormal(d,rng,wng);double weight=PPClamp(region.y+_PreparedCoverageParameters[2].z,0,1);if(weight<=0||weight>=1){wg=0;wng=0;}
 if(_PreparedCoverageParameters[4].y>0){double3 hg,hn;float4 hr;double high=PCBankSphere(d,2,1,0,footprint,hg,hn,hr);
  if(weight<.3L){h=high;g=hg;ng=hn;weight/=.3L;wg/=.3L;wng/=.3L;}else{plain=high;pg=hg;pn=hn;weight=(weight-.3L)/.7L;wg/=.7L;wng/=.7L;}}
 double reference;double2 refSlope;PCReference(d,reference,refSlope);double3 east,north;PPFrame(d,east,north);double3 refGradient=east*refSlope.x+north*refSlope.y;
 double rs=_PreparedCoverageParameters[2].y,hs=_PreparedCoverageParameters[1].w;
 g=rs*(rg-refGradient)+hs*(pg*(1-weight)+g*weight+(h-plain)*wg);
 ng=rs*(rng-refGradient)+hs*(pn*(1-weight)+ng*weight+(h-plain)*wng);
 PreparedPlanetSurface r=(PreparedPlanetSurface)0;r.height=baseHeight+rs*(region.x-reference)+hs*(plain*(1-weight)+h*weight);r.gradient=baseGradient+double2(PPDot(g,east),PPDot(g,north));
 double2 ns=baseGradient+double2(PPDot(ng,east),PPDot(ng,north));r.normal=(float3)PPNormalize(d-(_PreparedPlanetParameters[0].x/(_PreparedPlanetParameters[0].x+r.height))*(east*ns.x+north*ns.y));r.ready=1;return r;
}
float4 PCMaterial(double3 d,double footprint,float4 background)
{
 double3 g,n,rg,wg;float4 land,plain;PCBankSphere(d,0,1,0,footprint,g,n,land);PCBankSphere(d,1,1,0,footprint,g,n,plain);double2 region=PCRegion(d,rg,wg);
 float weight=saturate((float)region.y+_PreparedCoverageParameters[2].z);
 if(_PreparedCoverageParameters[4].y>0){float4 high;PCBankSphere(d,2,1,0,footprint,g,n,high);if(weight<.3){land=high;weight/=.3;}else{plain=high;weight=(weight-.3)/.7;}}
 float4 roles=max(0,lerp(plain,land,weight));roles/=max(dot(roles,1),1e-8);
 return roles;
}
#endif
