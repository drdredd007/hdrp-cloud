#ifndef SPACERUNNER_TERRAIN_MATERIAL_INCLUDED
#define SPACERUNNER_TERRAIN_MATERIAL_INCLUDED
TEXTURE2D(_PlanetLayerAlbedo0);TEXTURE2D(_PlanetLayerAlbedo1);TEXTURE2D(_PlanetLayerAlbedo2);TEXTURE2D(_PlanetLayerAlbedo3);
TEXTURE2D(_PlanetLayerNormal0);TEXTURE2D(_PlanetLayerNormal1);TEXTURE2D(_PlanetLayerNormal2);TEXTURE2D(_PlanetLayerNormal3);
TEXTURE2D(_PlanetLayerMask0);TEXTURE2D(_PlanetLayerMask1);TEXTURE2D(_PlanetLayerMask2);TEXTURE2D(_PlanetLayerMask3);
SAMPLER(sampler_LinearRepeat);
float4 _PlanetLayerTint0,_PlanetLayerTint1,_PlanetLayerTint2,_PlanetLayerTint3;
float4 _PlanetTexturePhase0,_PlanetTexturePhase1,_PlanetTexturePhase2,_PlanetTexturePhase3;
float4 _PlanetLayerControl0,_PlanetLayerControl1,_PlanetLayerControl2,_PlanetLayerControl3;
float4 _PlanetLayerPbr0,_PlanetLayerPbr1,_PlanetLayerPbr2,_PlanetLayerPbr3;
float4 _PlanetMaterialControls;
float4 _PlanetNormalVarianceControls;
float4 _PlanetVariationPhase,_PlanetVariationCell,_PlanetVariationControls;
float4x4 _PlanetLocalToRender,_PlanetRenderToLocal;
struct PlanetTerrainLayerSample {float3 albedo,gradient;float metallic,ao,smoothness,height;};
struct PlanetTerrainMaterialSample {float3 albedo,normal;float metallic,ao,smoothness;};
uint PlanetTerrainVariationHash(uint3 cell)
{
    cell&=65535u;uint seed=(uint)_PlanetVariationCell.w|((uint)_PlanetVariationControls.z<<16);
    uint h=cell.x*73856093u^cell.y*19349663u^cell.z*83492791u^seed;
    h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;return h^(h>>16);
}
float3 PlanetTerrainVariation(float3 offset)
{
    float3 coordinate=offset*_PlanetVariationPhase.w+_PlanetVariationPhase.xyz;
    int3 localCell=(int3)floor(coordinate);float3 f=frac(coordinate);f=f*f*f*(f*(f*6-15)+10);
    float3 value=0;
    [unroll]for(uint z=0;z<2;z++)[unroll]for(uint y=0;y<2;y++)[unroll]for(uint x=0;x<2;x++)
    {
        uint h=PlanetTerrainVariationHash((uint3)((int3)_PlanetVariationCell.xyz+localCell+int3(x,y,z)));
        float3 random=float3(h&1023u,(h>>10)&1023u,(h>>20)&1023u)/1023.0;
        float weight=(x?f.x:1-f.x)*(y?f.y:1-f.y)*(z?f.z:1-f.z);value+=random*weight;
    }
    // Fade unresolved macro variation instead of aliasing it from orbit.
    float footprint=max(length(ddx(coordinate)),length(ddy(coordinate)));
    return (value*2-1)*saturate(1-footprint*2);
}
float4 PlanetTriplanarSample(Texture2D textureValue,float3 uv,float3 axisWeight,float3 dx,float3 dy)
{
    // Planet-local metric coordinates retain derivatives across floating-origin changes.
    return SAMPLE_TEXTURE2D_GRAD(textureValue,sampler_LinearRepeat,uv.zy,dx.zy,dy.zy)*axisWeight.x+
        SAMPLE_TEXTURE2D_GRAD(textureValue,sampler_LinearRepeat,uv.xz,dx.xz,dy.xz)*axisWeight.y+
        SAMPLE_TEXTURE2D_GRAD(textureValue,sampler_LinearRepeat,uv.xy,dx.xy,dy.xy)*axisWeight.z;
}
float3 PlanetNormalSlope(float4 packed)
{
    // Unity's RG/AG normal encodings, including uncompressed runtime diagnostic textures.
    packed.r*=packed.a;float2 xy=packed.rg*2-1;float z=sqrt(max(1-dot(xy,xy),1e-4));
    return float3(-xy/z,0);
}
PlanetTerrainLayerSample PlanetTerrainReadLayer(Texture2D albedoMap,Texture2D normalMap,Texture2D maskMap,
    float4 tint,float4 phase,float4 control,float4 pbr,float varianceScale,float3 offset,float3 axisWeight,float normalStrength,float3 warp)
{
    PlanetTerrainLayerSample result=(PlanetTerrainLayerSample)0;
    float3 uv=offset*phase.w+phase.xyz+warp,dx=ddx(offset)*phase.w+ddx(warp),dy=ddy(offset)*phase.w+ddy(warp);
    result.albedo=PlanetTriplanarSample(albedoMap,uv,axisWeight,dx,dy).rgb*tint.rgb;
    float4 mask=PlanetTriplanarSample(maskMap,uv,axisWeight,dx,dy);
    result.metallic=lerp(pbr.x,mask.r,control.w);result.ao=lerp(pbr.y,mask.g,control.w);
    result.smoothness=lerp(pbr.z,mask.a,control.w);result.height=mask.b*control.y+control.z;
    if(pbr.w>0&&normalStrength>0)
    {
        float4 nx=SAMPLE_TEXTURE2D_GRAD(normalMap,sampler_LinearRepeat,uv.zy,dx.zy,dy.zy);
        float4 ny=SAMPLE_TEXTURE2D_GRAD(normalMap,sampler_LinearRepeat,uv.xz,dx.xz,dy.xz);
        float4 nz=SAMPLE_TEXTURE2D_GRAD(normalMap,sampler_LinearRepeat,uv.xy,dx.xy,dy.xy);
        bool moments=pbr.w>1.5;
        float3 sx=moments?float3(nx.rg,0):PlanetNormalSlope(nx);
        float3 sy=moments?float3(ny.rg,0):PlanetNormalSlope(ny);
        float3 sz=moments?float3(nz.rg,0):PlanetNormalSlope(nz);
        result.gradient=(float3(0,sx.y,sx.x)*axisWeight.x+float3(sy.x,0,sy.y)*axisWeight.y+
            float3(sz.x,sz.y,0)*axisWeight.z)*(control.x*normalStrength);
        if(moments && varianceScale>0)
        {
            // Loss of unresolved gradients broadens the lobe instead of making it glossy.
            // Independent-axis, isotropic bounded approximation; GGX has no finite full second moment.
            float3 variance=max(float3(nx.b-dot(nx.rg,nx.rg),ny.b-dot(ny.rg,ny.rg),nz.b-dot(nz.rg,nz.rg)),0);
            float amplitude=control.x*normalStrength;
            float filteredVariance=dot(variance,axisWeight*axisWeight)*amplitude*amplitude*varianceScale;
            float roughness=1-saturate(result.smoothness);
            float alpha=roughness*roughness;
            result.smoothness=1-sqrt(sqrt(saturate(alpha*alpha+filteredVariance)));
        }
    }
    return result;
}
PlanetTerrainMaterialSample PlanetTerrainEvaluate(float3 offset,float3 normalPlanet,float4 masks,float normalStrength)
{
    PlanetTerrainMaterialSample result=(PlanetTerrainMaterialSample)0;
    float3 variation=0;
    if(_PlanetVariationControls.x>0||_PlanetVariationControls.y>0)variation=PlanetTerrainVariation(offset);
    float3 warp=variation*_PlanetVariationControls.x;
    float3 axisWeight=pow(abs(normalPlanet),_PlanetMaterialControls.y);
    axisWeight/=max(dot(axisWeight,1),1e-6);
    masks=max(masks,0);float sum=dot(masks,1);masks=sum>1e-6?masks/sum:float4(0,0,1,0);
    PlanetTerrainLayerSample a=PlanetTerrainReadLayer(_PlanetLayerAlbedo0,_PlanetLayerNormal0,_PlanetLayerMask0,_PlanetLayerTint0,_PlanetTexturePhase0,_PlanetLayerControl0,_PlanetLayerPbr0,_PlanetNormalVarianceControls.x,offset,axisWeight,normalStrength,warp);
    PlanetTerrainLayerSample b=PlanetTerrainReadLayer(_PlanetLayerAlbedo1,_PlanetLayerNormal1,_PlanetLayerMask1,_PlanetLayerTint1,_PlanetTexturePhase1,_PlanetLayerControl1,_PlanetLayerPbr1,_PlanetNormalVarianceControls.y,offset,axisWeight,normalStrength,warp);
    PlanetTerrainLayerSample c=PlanetTerrainReadLayer(_PlanetLayerAlbedo2,_PlanetLayerNormal2,_PlanetLayerMask2,_PlanetLayerTint2,_PlanetTexturePhase2,_PlanetLayerControl2,_PlanetLayerPbr2,_PlanetNormalVarianceControls.z,offset,axisWeight,normalStrength,warp);
    PlanetTerrainLayerSample d=PlanetTerrainReadLayer(_PlanetLayerAlbedo3,_PlanetLayerNormal3,_PlanetLayerMask3,_PlanetLayerTint3,_PlanetTexturePhase3,_PlanetLayerControl3,_PlanetLayerPbr3,_PlanetNormalVarianceControls.w,offset,axisWeight,normalStrength,warp);
    float4 heights=float4(a.height,b.height,c.height,d.height);
    float highest=max(max(masks.x>0?heights.x:-1e20,masks.y>0?heights.y:-1e20),max(masks.z>0?heights.z:-1e20,masks.w>0?heights.w:-1e20));
    float4 weights=masks*saturate((heights-highest+_PlanetMaterialControls.x)/max(_PlanetMaterialControls.x,1e-6));
    weights/=max(dot(weights,1),1e-6);
    result.albedo=(a.albedo*weights.x+b.albedo*weights.y+c.albedo*weights.z+d.albedo*weights.w)*(1+variation.z*_PlanetVariationControls.y);
    result.metallic=dot(weights,float4(a.metallic,b.metallic,c.metallic,d.metallic));
    result.ao=dot(weights,float4(a.ao,b.ao,c.ao,d.ao));
    result.smoothness=dot(weights,float4(a.smoothness,b.smoothness,c.smoothness,d.smoothness));
    float3 gradient=a.gradient*weights.x+b.gradient*weights.y+c.gradient*weights.z+d.gradient*weights.w;
    gradient-=normalPlanet*dot(normalPlanet,gradient);
    result.normal=normalize(normalPlanet-gradient);return result;
}
#endif
