// Derived from js/precipitation.js and js/heuristic-precip.js.
// World Orogen cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
StructuredBuffer<float4> _ClimateSeasonWind,_ClimateSeasonOcean;
StructuredBuffer<float2> _ClimateBlendWind,_ClimateElevGrad;
RWStructuredBuffer<float2> _ClimateBlendWindWrite;
StructuredBuffer<float3> _ClimateWind3D;
RWStructuredBuffer<float3> _ClimateWind3DWrite;
StructuredBuffer<float> _ClimateMoisture,_ClimateConvergence,_ClimateHeightKm,_ClimateHeuristicWest;
RWStructuredBuffer<float> _ClimateHeightKmWrite,_ClimateHeuristicWestWrite;
RWStructuredBuffer<float2> _ClimatePrecipWrite;
StructuredBuffer<float> _ClimateSeasonPrecipSummer,_ClimateSeasonPrecipWinter;
double2 ClimateHeuristicWind(double distance,bool north)
{
 double sign=north?1.0L:-1.0L,e,n;
 if(distance<5){e=0;n=-sign*.1L;}
 else if(distance<30){double t=ClimateStep(5,15,distance)*(1.0L-ClimateStep(25,32,distance));e=-t*.8L;n=-sign*t*.3L;}
 else if(distance<60){double t=ClimateStep(30,40,distance)*(1.0L-ClimateStep(55,65,distance));e=t*.9L;n=sign*t*.25L;}
 else{double t=ClimateStep(60,70,distance);e=-t*.4L;n=-sign*t*.15L;}
 return double2(e,n);
}
double ClimateZonalBase(double d)
{
 if(d<5)return 1;
 if(d<10)return 1.0L-(1.0L-HEUR_ZONAL_TRADE_VALUE)*ClimateStep(5,10,d);
 if(d<HEUR_ZONAL_DRY_POLEWARD_DEG)return HEUR_ZONAL_TRADE_VALUE-(HEUR_ZONAL_TRADE_VALUE-HEUR_ZONAL_DESERT_MIN)*ClimateStep(10,HEUR_ZONAL_DESERT_END_DEG,d);
 if(d<HEUR_ZONAL_WESTERLY_PEAK_DEG)return HEUR_ZONAL_DESERT_MIN+(HEUR_ZONAL_WESTERLY_PEAK-HEUR_ZONAL_DESERT_MIN)*ClimateStep(HEUR_ZONAL_DRY_POLEWARD_DEG,HEUR_ZONAL_WESTERLY_PEAK_DEG,d);
 if(d<70)return HEUR_ZONAL_WESTERLY_PEAK-.2L*ClimateStep(HEUR_ZONAL_WESTERLY_PEAK_DEG,70,d);
 return HEUR_ZONAL_WESTERLY_PEAK-.2L-(HEUR_ZONAL_WESTERLY_PEAK-.2L-HEUR_ZONAL_POLAR_MIN)*ClimateStep(70,90,d);
}
[numthreads(64,1,1)] void PrecipTerrain(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount){_ClimateWrite[r]=float4(_WorldElevation[r],0,0,0);_ClimateHeightKmWrite[r]=(float)ClimateHeight(max((double)_WorldElevation[r],0.0L));}}
[numthreads(64,1,1)] void PrecipElevationBlend(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount)_ClimateWrite[r]=float4((float)((double)_ClimateRead[r].x*.6L+(double)_WorldElevation[r]*.4L),0,0,0);}
[numthreads(64,1,1)] void PrecipWind(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double sd=(double)_ClimateGeo[r].x-ClimateLookup(_ClimateGeo[r].y)*HEUR_ITCZ_SHIFT_DAMPEN;
 double2 h=ClimateHeuristicWind(abs(sd)/ClimateDeg,sd>0),w=(double2)_ClimateSeasonWind[r].xy;
 float2 stored=(float2)(w*.5L+(double2)(float2)h*.5L);_ClimateBlendWindWrite[r]=stored;
 _ClimateWind3DWrite[r]=(float3)((double3)_ClimateEast[r]*(double)stored.x+(double3)_ClimateNorth[r]*(double)stored.y);
}
[numthreads(64,1,1)] void PrecipConvergence(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double3 w=(double3)_ClimateWind3D[r],p=(double3)_WorldPositions[r];double sum=0;uint count=0;
 for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];double3 d=(double3)_WorldPositions[nb]-p,nw=(double3)_ClimateWind3D[nb];sum-=dot(nw+w,d);count++;}
 _ClimateScalarWrite[r]=count>0?(float)(sum/count):0;
}
[numthreads(64,1,1)] void MoistureInitialize(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double moisture=0;
 if(_ClimateLand[r]==0)moisture=PRECIP_OCEAN_MOISTURE_BASE+.35L*max(0.0L,(double)_ClimateSeasonOcean[r].w);
 else if(_ClimateCoastLand[r]==0)
 {double warmth=0;uint count=0;double3 ocean=0,p=(double3)_WorldPositions[r];for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];if(_ClimateLand[nb]!=0)continue;count++;warmth+=(double)_ClimateSeasonOcean[nb].w;ocean+=(double3)_WorldPositions[nb]-p;}if(count>0){double on=dot((double3)_ClimateWind3D[r],ocean)<0?1.0L:.25L;moisture=on*(.5L+.5L*clamp(warmth/count,-.8L,1.0L));}}
 _ClimateScalarWrite[r]=(float)moisture;
}
[numthreads(64,1,1)] void MoistureAdvect(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double current=_ClimateScalarRead[r];double2 w=(double2)_ClimateBlendWind[r];
 if(_ClimateLand[r]==0||w.x*w.x+w.y*w.y<1e-6L){_ClimateScalarWrite[r]=(float)current;return;}
 double moisture=0,weight=0,height=0;double3 p=(double3)_WorldPositions[r];
 for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];double d=dot((double3)_ClimateWind3D[nb],p-(double3)_WorldPositions[nb]);if(d>0){moisture+=(double)_ClimateScalarRead[nb]*d;height+=(double)_ClimateHeightKm[nb]*d;weight+=d;}}
 if(weight>0){double hops=_ClimateParameters[4],base=1.0L-(double)pow((float)PRECIP_ADVECT_FLAT_SURVIVAL,(float)(1.0L/hops)),gain=max(0.0L,(double)_ClimateHeightKm[r]-height/weight)*hops,depletion=base+min(.8L,gain*PRECIP_ELEV_DEPLETION_PER_KM);current=max(current,moisture/weight*max(0.0L,1.0L-depletion));}
 _ClimateScalarWrite[r]=(float)current;
}
[numthreads(64,1,1)] void PrecipMechanisms(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;
 double lat=_ClimateGeo[r].x,lon=_ClimateGeo[r].y,absLat=abs(lat)/ClimateDeg,elev=_WorldElevation[r],moisture=_ClimateMoisture[r],p=moisture,avg=ClimatePi*6371.0L/ClimateSqrt((double)_WorldRegionCount),edge=ClimatePi/ClimateSqrt((double)_WorldRegionCount),hops=_ClimateParameters[4],height=_ClimateHeightKm[r];
 bool land=_ClimateLand[r]!=0,summer=_ClimateSeason==0?lat>=0:lat<0;double cont=land?(double)_ClimateCont[r]:0;
 double itcz=ClimateLookup(lon),dist=abs(lat-itcz)/ClimateDeg;
 if(dist<PRECIP_ITCZ_WIDTH_DEG){double strength=ClimateStep(PRECIP_ITCZ_WIDTH_DEG,0,dist),boost=dist<5?PRECIP_ITCZ_CORE_BOOST:1.0L;p=p*(1.0L+strength*boost)+strength*PRECIP_ITCZ_ADDITIVE;}
 double conv=_ClimateConvergence[r];if(conv>0){double strength=min(1.0L,conv/edge*.055L);p=p*(1.0L+strength*PRECIP_CONV_MULT_BOOST)+strength*moisture*PRECIP_CONV_ADD_FRAC;}
 double2 w=(double2)_ClimateBlendWind[r],grad=(double2)_ClimateElevGrad[r];double wg=w.x*grad.x+w.y*grad.y;
 if(land&&elev>0){if(wg>0)p+=min(1.0L,wg*15.0L)*PRECIP_ORO_UPLIFT_ADD;else p*=max(.02L,1.0L-min(1.0L,-wg*18.0L)*PRECIP_ORO_SHADOW_MAX_SUPPRESS);}
 double peak=summer?PRECIP_SUBTROP_PEAK_SUMMER:PRECIP_SUBTROP_PEAK_WINTER,center=summer?PRECIP_SUBTROP_CENTER_SUMMER_DEG:PRECIP_SUBTROP_CENTER_WINTER_DEG,width=summer?PRECIP_SUBTROP_WIDTH_SUMMER_DEG:PRECIP_SUBTROP_WIDTH_WINTER_DEG;
 if(land&&summer){double poleward=lat>=0?w.y:-w.y;if(poleward>0){double cd=_ClimateCoastLand[r]>=0?_ClimateCoastLand[r]:hops,proximity=1.0L-ClimateStep(0,hops*.4L,cd),relief=ClimateStep(0,.15L,poleward)*proximity;peak*=1.0L-relief*PRECIP_MONSOON_RELIEF_MAX;}}
 if(land)peak*=1.0L-max(0.0L,-(double)_ClimateWestness[r])*PRECIP_SUBTROP_EAST_RELIEF;
 double bd=abs(absLat-center),band=bd<width?ClimateStep(width,0,bd)*peak:0,dev=_ClimateSeasonWind[r].w,pm=dev>0?ClimateStep(0,12,dev)*.25L:-ClimateStep(0,15,-dev)*.2L,total=max(0.0L,band+pm);p*=total>0?max(.05L,1.0L-total):1.0L-total;
 if(summer&&land&&PRECIP_MONSOON_ADD>0){double poleward=(lat>=0?lat-itcz:itcz-lat)/ClimateDeg;if(poleward>0&&poleward<PRECIP_MONSOON_REACH_DEG){double cd=_ClimateCoastLand[r]>=0?_ClimateCoastLand[r]:hops;p+=PRECIP_MONSOON_ADD*ClimateStep(PRECIP_MONSOON_REACH_DEG,0,poleward)*(1.0L-ClimateStep(0,hops,cd));}}
 if(absLat>40){double strength=ClimateStep(40,70,absLat),cd=_ClimateCoastLand[r]<0?hops:_ClimateCoastLand[r],fade=1.0L-ClimateStep(0,hops,cd);p+=strength*PRECIP_POLAR_BASE_ADD+strength*PRECIP_POLAR_COASTAL_ADD*fade;p*=1.0L+strength*.15L;}
 if(land&&cont>0)p*=max(.03L,1.0L-cont*cont*PRECIP_CONT_DRYNESS);
 double lee=max(2.0L,(double)(int)(200.0L/avg+.5L));if(land&&height>1.5L&&wg<-.01L&&_ClimateCoastLand[r]>=0&&_ClimateCoastLand[r]<lee)p+=.15L*min(1.0L,height/5.0L);
 if(!land)p=max(p,.15L*(1.0L-(dev>0?ClimateStep(0,12,dev):0.0L)));
 if(land&&_ClimateCoastLand[r]>0){double d=(double)_ClimateCoastLand[r]*avg;if(d>PRECIP_COAST_CUTOFF_START_KM)p*=max(.03L,1.0L-ClimateStep(PRECIP_COAST_CUTOFF_START_KM,PRECIP_COAST_CUTOFF_END_KM,d));}
 p*=1.0L+_ClimateParameters[5]*.5L;if(_ClimateParameters[6]>.4L){double t=(_ClimateParameters[6]-.4L)/.6L;p*=1.0L-t*t*.98L;}
 double seed=0;if(land&&elev>0&&height>=.8L){double scale=min(1.0L,(height-.5L)/2.5L);seed=wg>0?min(1.0L,wg*20.0L)*scale:wg<0?-min(1.0L,-wg*18.0L)*scale:0;}
 float stored=(float)seed;_ClimateWrite[r]=float4((float)max(0.0L,p),stored,stored,stored);
}
[numthreads(64,1,1)] void ShadowPropagate(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;float4 current=_ClimateRead[r];double sum=0,weight=0;bool shadow=_ClimateMode==0;
 if(_ClimateLand[r]!=0){double3 p=(double3)_WorldPositions[r],wind=(double3)_ClimateWind3D[r];for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];double3 d=p-(double3)_WorldPositions[nb];double raw=shadow?dot((double3)_ClimateWind3D[nb],d):-dot(wind,d);if(raw<=0)continue;double storedWeight=(double)(float)raw,val=shadow?(double)_ClimateRead[nb].y:(double)_ClimateRead[nb].z;if(shadow?val<0:val>0){sum+=val*storedWeight;weight+=storedWeight;}}}
 if(weight>0){double carried=sum/weight*_ClimateParameters[7];if(shadow)current.y=(float)min((double)current.y,carried);else current.z=(float)max((double)current.z,carried);}
 _ClimateWrite[r]=current;
}
[numthreads(64,1,1)] void ShadowMerge(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount){float4 v=_ClimateRead[r];v.w=v.y<0?v.y:v.z;_ClimateWrite[r]=v;}}
[numthreads(64,1,1)] void ShadowApply(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;float4 v=_ClimateRead[r];double p=v.x,avg=ClimatePi*6371.0L/ClimateSqrt((double)_WorldRegionCount);
 if(_ClimateLand[r]!=0)
 {double rs=v.w;if(rs<-.01L)p*=max(.02L,1.0L-min(1.0L,-rs*PRECIP_RS_APPLY_STRENGTH_SCALE)*PRECIP_RS_APPLY_MAX_SUPPRESS);else if(rs>.01L)p+=rs*PRECIP_RS_APPLY_WINDWARD_ADD;
  double reach=max(2.0L,(double)(int)(300.0L/avg+.5L));int cd=_ClimateCoastLand[r];if(cd>=0&&cd<=reach){double sum=0;uint count=0;for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];if(_ClimateLand[nb]==0){sum+=(double)_ClimateSeasonOcean[nb].w;count++;}}if(count>0){double warmth=sum/count,fade=1.0L-cd/reach;p*=warmth<0?max(.05L,1.0L+warmth*PRECIP_COLD_CURRENT_SUPPRESS*fade):1.0L+warmth*PRECIP_WARM_CURRENT_BOOST*fade;}}
 }
 v.x=(float)p;_ClimateWrite[r]=v;
}
[numthreads(64,1,1)] void HeuristicWest(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double value=0;
 if(_ClimateLand[r]!=0&&_ClimateCoastLand[r]==0){double3 p=(double3)_WorldPositions[r],east=(double3)_ClimateEast[r];double sum=0;uint count=0;for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];if(_ClimateLand[nb]==0){sum+=dot((double3)_WorldPositions[nb]-p,east);count++;}}if(count>0)value=sum<0?1.0L:-1.0L;}
 _ClimateHeuristicWestWrite[r]=(float)value;
}
[numthreads(64,1,1)] void HeuristicPrecipitation(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double lat=_ClimateGeo[r].x,itcz=ClimateLookup(_ClimateGeo[r].y)*HEUR_ITCZ_SHIFT_DAMPEN,sd=lat-itcz,d=abs(sd)/ClimateDeg,ad=abs(lat)/ClimateDeg,avg=ClimatePi*6371.0L/ClimateSqrt((double)_WorldRegionCount);bool summer=_ClimateSeason==0?lat>=0:lat<0,land=_ClimateLand[r]!=0;
 double season=summer?HEUR_SEASON_SUMMER_MOD:HEUR_SEASON_WINTER_MOD;if(summer&&ad>22&&ad<45){double suppress=ClimateStep(22,30,ad)*(1.0L-ClimateStep(38,45,ad)),strength=HEUR_MED_SUPPRESS_BASE+(double)_ClimateHeuristicWest[r]*HEUR_MED_WESTCOAST_BONUS;season*=1.0L-suppress*max(0.0L,strength);}
 double cont=land?(double)_ClimateCont[r]:0,cm=cont>0?1.0L-cont*cont*HEUR_CONT_DRYNESS:1.0L,oro=1;
 if(land&&_WorldElevation[r]>0){double2 w=ClimateHeuristicWind(d,sd>0),g=(double2)_ClimateElevGrad[r];double wg=w.x*g.x+w.y*g.y;if(wg>0)oro=1.0L+min(1.0L,wg*15.0L)*HEUR_ORO_UPLIFT_MAX;else oro=max(.3L,1.0L-min(1.0L,-wg*18.0L)*HEUR_ORO_SHADOW_MAX*min(1.0L,ClimateHeight((double)_WorldElevation[r])/3.0L));}
 double dist=1;if(land&&_ClimateCoastLand[r]>0){double km=(double)_ClimateCoastLand[r]*avg;if(km>2000)dist=max(.03L,1.0L-ClimateStep(2000,3000,km));}
 _ClimateWrite[r]=float4((float)max(.05L,ClimateZonalBase(d)*season*cm*oro*dist),0,0,0);
}
[numthreads(64,1,1)] void PrecipBlend(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount)_ClimateWrite[r]=float4((float)(PRECIP_MODEL_BLEND*(double)_ClimateRead[r].x+(1.0L-PRECIP_MODEL_BLEND)*(double)_ClimateScalarRead[r]),0,0,0);}
[numthreads(64,1,1)] void PrecipNormalize(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double p=min(1.0L,(double)_ClimateRead[r].x/_ClimateParameters[0]);if(_ClimateLand[r]!=0&&_ClimateCont[r]>PRECIP_CONT_CAP_FADE_START)p=min(p,1.0L-ClimateStep(PRECIP_CONT_CAP_FADE_START,1,(double)_ClimateCont[r])*PRECIP_CONT_CAP_MAX_REDUCTION);
 _ClimateScalarWrite[r]=(float)p;
}
[numthreads(64,1,1)] void PrecipContrast(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double ps=_ClimateSeasonPrecipSummer[r],pw=_ClimateSeasonPrecipWinter[r],mean=(ps+pw)*.5L;
 // Source writes ps then calculates pw from original pw and the saved mean.
 _ClimatePrecipWrite[r]=float2((float)max(0.0L,mean+(ps-mean)*PRECIP_SEASON_CONTRAST),(float)max(0.0L,mean+(pw-mean)*PRECIP_SEASON_CONTRAST));
}
