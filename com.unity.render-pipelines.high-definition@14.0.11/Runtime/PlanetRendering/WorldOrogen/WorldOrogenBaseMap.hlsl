#ifndef SPACERUNNER_OROGEN_BASE_MAP_INCLUDED
#define SPACERUNNER_OROGEN_BASE_MAP_INCLUDED
TEXTURE2D_ARRAY(_OrogenBaseColour);
SAMPLER(sampler_OrogenBaseColour);
float _OrogenBaseColourEnabled,_OrogenInverseRadius;
float4 _OrogenBaseGrid;
float3 _OrogenBaseAnchor;
float4x4 _OrogenRenderToLocal;
float3 OrogenBaseColour(float3 direction)
{
    float3 a=abs(direction);float2 uv;uint face;
    if(a.x>=a.y&&a.x>=a.z){float3 c=direction/a.x;face=direction.x>0?0:1;uv=float2(direction.x>0?-c.z:c.z,c.y);}
    else if(a.y>=a.z){float3 c=direction/a.y;face=direction.y>0?2:3;uv=float2(c.x,direction.y>0?-c.z:c.z);}
    else{float3 c=direction/a.z;face=direction.z>0?4:5;uv=float2(direction.z>0?c.x:-c.x,c.y);}
    uv=((uv*.5+.5)*_OrogenBaseGrid.x+.5)/_OrogenBaseGrid.y;
    return SAMPLE_TEXTURE2D_ARRAY(_OrogenBaseColour,sampler_OrogenBaseColour,uv,face).rgb;
}
#endif
