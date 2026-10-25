#ifndef PLANET_PERIODIC_SURFACE_INCLUDED
#define PLANET_PERIODIC_SURFACE_INCLUDED
TEXTURE2D(_PeriodicSnowCoverage);SAMPLER(sampler_PeriodicSnowCoverage);
TEXTURE2D_ARRAY(_PeriodicDetailColor);SAMPLER(sampler_PeriodicDetailColor);
TEXTURE2D_ARRAY(_PeriodicDetailSlopes);SAMPLER(sampler_PeriodicDetailSlopes);
float _PeriodicSurfaceEnabled;
float4 _PeriodicSnowColor,_PeriodicRockColor,_PeriodicSnowMean,_PeriodicRockMean;
float4 _PeriodicDetailControls,_PeriodicSmoothness;
TEXTURE2D(_PeriodicLandformRoles);
TEXTURE2D(_PeriodicPlainsRoles);
float _PeriodicMaterialEnabled,_PeriodicMaterialView;
float4 _PeriodicBackgroundLayers;
float3 PeriodicSurfaceWeights(float3 d)
{float3 w=d*d;w*=w;w*=w;w*=w;return w/(w.x+w.y+w.z);}
float4 PeriodicMaterialLayers(float3 radialWorld,out float4 roles)
{
    float3 d=normalize(mul((float3x3)_PeriodicWorldToLocal,radialWorld));
    float3 p=d*_PeriodicNormalCycles,w=PeriodicSurfaceWeights(d);
    roles=w.x*SAMPLE_TEXTURE2D(_PeriodicLandformRoles,sampler_PeriodicNormalSlopes,p.zy)+
        w.y*SAMPLE_TEXTURE2D(_PeriodicLandformRoles,sampler_PeriodicNormalSlopes,p.xz)+
        w.z*SAMPLE_TEXTURE2D(_PeriodicLandformRoles,sampler_PeriodicNormalSlopes,p.xy);
    if(_PeriodicMacroSide>0){
        float4 plains=w.x*SAMPLE_TEXTURE2D(_PeriodicPlainsRoles,sampler_PeriodicNormalSlopes,p.zy)+
            w.y*SAMPLE_TEXTURE2D(_PeriodicPlainsRoles,sampler_PeriodicNormalSlopes,p.xz)+
            w.z*SAMPLE_TEXTURE2D(_PeriodicPlainsRoles,sampler_PeriodicNormalSlopes,p.xy);
        float3 a=abs(d);float2 uv;int face;
        if(a.x>=a.y&&a.x>=a.z){face=d.x>=0?0:1;uv=float2(d.x>=0?-d.z:d.z,d.y)/a.x;}
        else if(a.y>=a.z){face=d.y>=0?2:3;uv=float2(d.x,d.y>=0?-d.z:d.z)/a.y;}
        else{face=d.z>=0?4:5;uv=float2(d.z>=0?d.x:-d.x,d.y)/a.z;}
        uv=((uv*.5+.5)*(_PeriodicMacroSide-1)+.5)/_PeriodicMacroSide;
        float weight=SAMPLE_TEXTURE2D_ARRAY_LOD(_PeriodicMacroNormals,sampler_PeriodicMacroNormals,uv,face,0).y;
        roles=lerp(plains,roles,weight);
    }
    roles=max(roles,0);roles/=max(dot(roles,1),1e-8);
    // Same physical role conversion as prepared placement masks. Alpha is exposed rock.
    float4 bg=_PeriodicBackgroundLayers;
    return roles.x*bg+roles.y*lerp(bg,float4(0,1,0,0),.35)+
        roles.z*lerp(bg,float4(0,.45,.55,0),.65)+roles.w*float4(0,0,1,0);
}
float4 PeriodicDetailColor(float3 p,float3 w,float slice)
{
    return w.x*SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailColor,sampler_PeriodicDetailColor,p.zy,slice)+
        w.y*SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailColor,sampler_PeriodicDetailColor,p.xz,slice)+
        w.z*SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailColor,sampler_PeriodicDetailColor,p.xy,slice);
}
float3 PeriodicDetailGradient(float3 p,float3 w,float slice)
{
    float2 x=SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailSlopes,sampler_PeriodicDetailSlopes,p.zy,slice).rg;
    float2 y=SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailSlopes,sampler_PeriodicDetailSlopes,p.xz,slice).rg;
    float2 z=SAMPLE_TEXTURE2D_ARRAY(_PeriodicDetailSlopes,sampler_PeriodicDetailSlopes,p.xy,slice).rg;
    return w.x*float3(0,x.y,x.x)+w.y*float3(y.x,0,y.y)+w.z*float3(z.x,z.y,0);
}
void PeriodicSnowAndRock(float3 radialWorld,float3 detail,inout float3 normal,out float3 albedo,out float smoothness)
{
    float3 d=normalize(mul((float3x3)_PeriodicWorldToLocal,radialWorld));
    float3 w=PeriodicSurfaceWeights(d),p=d*_PeriodicNormalCycles;
    // Coverage is imported from the full-resolution source slope, never from the current mesh normal.
    float snow=saturate(w.x*SAMPLE_TEXTURE2D(_PeriodicSnowCoverage,sampler_PeriodicSnowCoverage,p.zy).r+
        w.y*SAMPLE_TEXTURE2D(_PeriodicSnowCoverage,sampler_PeriodicSnowCoverage,p.xz).r+
        w.z*SAMPLE_TEXTURE2D(_PeriodicSnowCoverage,sampler_PeriodicSnowCoverage,p.xy).r);
    // Detail coordinates use the renderer's periodic planet-local origin, shared by far and near meshes.
    float3 ps=detail/_PeriodicDetailControls.x,pr=detail/_PeriodicDetailControls.y;
    float4 cs=PeriodicDetailColor(ps,w,0),cr=PeriodicDetailColor(pr,w,1);
    float3 snowAlbedo=saturate(_PeriodicSnowColor.rgb*cs.rgb/max(_PeriodicSnowMean.rgb,.001));
    float3 rockAlbedo=saturate(_PeriodicRockColor.rgb*cr.rgb/max(_PeriodicRockMean.rgb,.001));
    albedo=lerp(rockAlbedo,snowAlbedo,snow);
    smoothness=saturate(lerp(_PeriodicSmoothness.y*cr.a,_PeriodicSmoothness.x*cs.a,snow));
    float3 gradient=lerp(PeriodicDetailGradient(pr,w,1)*_PeriodicDetailControls.w,
        PeriodicDetailGradient(ps,w,0)*_PeriodicDetailControls.z,snow);
    float3 n=normalize(mul((float3x3)_PeriodicWorldToLocal,normal));
    gradient-=n*dot(gradient,n);
    normal=normalize(mul((float3x3)_PeriodicLocalToWorld,normalize(n-gradient)));
}
#endif
