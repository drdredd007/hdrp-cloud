#ifndef SPACERUNNER_PLANET_PATCH_MORPH_INCLUDED
#define SPACERUNNER_PLANET_PATCH_MORPH_INCLUDED
struct PlanetVertex {float3 position;float3 normal;float4 color;};
StructuredBuffer<PlanetVertex> _PlanetVertices,_PlanetParentVertices;
int _PlanetBaseVertex,_PlanetStitchMask,_PlanetFilteredSurface;
            float PlanetParentBandWeight(uint id)
            {
                if(_PlanetFilteredSurface==0||_PlanetStitchMask==0)return 0;
                const uint resolution=32,row=33,main=row*row;
                uint x,y;
                if(id<main){x=id%row;y=id/row;}
                else
                {
                    uint local=id-main,edge=local/row,j=local%row;
                    x=edge==0?j:edge==1?resolution:edge==2?resolution-j:0;
                    y=edge==0?0:edge==1?j:edge==2?resolution:resolution-j;
                }
                uint mask=(uint)_PlanetStitchMask,distance=resolution;
                if((mask&1u)!=0)distance=min(distance,y);
                if((mask&2u)!=0)distance=min(distance,resolution-x);
                if((mask&4u)!=0)distance=min(distance,resolution-y);
                if((mask&8u)!=0)distance=min(distance,x);
                return saturate(1-(float)distance*0.5);
            }
            PlanetVertex PlanetFetch(uint id)
            {
                PlanetVertex v=_PlanetVertices[(uint)_PlanetBaseVertex+id];float weight=PlanetParentBandWeight(id);
                if(weight<=0)return v;
                PlanetVertex parent=_PlanetParentVertices[(uint)_PlanetBaseVertex+id];
                v.position=lerp(v.position,parent.position,weight);
                float3 mixedNormal=lerp(v.normal,parent.normal,weight);float mixedLength=dot(mixedNormal,mixedNormal);
                v.normal=mixedLength>1e-12?mixedNormal*rsqrt(mixedLength):0;
                // Missing input must reject a whole primitive rather than forming a partial alpha triangle.
                v.color.a=min(v.color.a,parent.color.a);return v;
            }
            // T-junction removal: an odd vertex on a stitched edge (and its skirt vertex) moves to the midpoint of its
            // even neighbours, which coincide with the coarser patch's edge vertices, so both sides share one edge line.
            uint PlanetStitchStep(uint id)
            {
                uint mask=(uint)_PlanetStitchMask;
                if(mask==0)return 0;
                const uint resolution=32,row=33,main=row*row;
                uint step=0;
                if(id<main)
                {
                    uint x=id%row,y=id/row;
                    if(y==0 && (x&1) && (mask&1))step=1;
                    else if(x==resolution && (y&1) && (mask&2))step=row;
                    else if(y==resolution && (x&1) && (mask&4))step=1;
                    else if(x==0 && (y&1) && (mask&8))step=row;
                }
                else if(id<main+4*row)
                {
                    uint local=id-main,edge=local/row,j=local%row;
                    if((j&1) && ((mask>>edge)&1))step=1;
                }
                return step;
            }
            PlanetVertex PlanetStitchedVertex(uint id)
            {
                PlanetVertex v=PlanetFetch(id);uint step=PlanetStitchStep(id);
                if(step==0)return v;
                PlanetVertex a=PlanetFetch(id-step),b=PlanetFetch(id+step);
                v.position=(a.position+b.position)*0.5;
                float3 mixedNormal=a.normal+b.normal;float mixedLength=dot(mixedNormal,mixedNormal);
                v.normal=mixedLength>1e-12?mixedNormal*rsqrt(mixedLength):0;
                v.color=(a.color+b.color)*0.5;
                return v;
            }
#endif
