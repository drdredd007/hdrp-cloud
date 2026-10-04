// Derived from World Orogen js/temperature.js.
// cc2662b4edd52231c4f65d8765f3ef12cd82d9b7; GPL-3.0-only.
StructuredBuffer<int> _ClimateLandComponents,_ClimateLandComponentSizes,_ClimateWarmCoastDistance;
StructuredBuffer<float4> _ClimateLandComponentStats;
StructuredBuffer<float2> _ClimateLandComponentWidth;
StructuredBuffer<double4> _ClimateZoneInputs;
StructuredBuffer<float> _ClimateTempCont;
RWStructuredBuffer<float> _ClimateTempContWrite;
StructuredBuffer<int> _ClimatePatchLabels,_ClimatePatchOffsets,_ClimatePatchMembers;
StructuredBuffer<float> _ClimatePatchZones;
RWStructuredBuffer<float2> _ClimateTemperatureWrite;
int _ClimatePatchCount,_ClimateOffset,_ClimateCount;
static const double ClimateSwingTable[35]={3,7.5L,13.5L,16.5L,19.5L,6,10.5L,18,24,31,7.5L,15,24,31.5L,38.5L,9,16.5L,28.5L,37.5L,46.5L,9,19.5L,31.5L,42,52.5L,9,21,36,45,57,9,22.5L,37.5L,48,60};
static const double ClimateLatitudeMids[7]={9.5L,24.5L,34.5L,44.5L,54.5L,64.5L,80};
double ClimateSwing(double latitude,double zone)
{
 uint li=0;for(uint i=0;i<6;i++)if(latitude>=ClimateLatitudeMids[i])li=i;
 uint li2=min(li+1,6u);double lt=li==li2?0.0L:clamp((latitude-ClimateLatitudeMids[li])/(ClimateLatitudeMids[li2]-ClimateLatitudeMids[li]),0.0L,1.0L),z=clamp(zone*4.0L,0.0L,4.0L);
 uint zi=min((uint)z,3u);double zt=z-zi,a=ClimateSwingTable[li*5+zi],b=ClimateSwingTable[li2*5+zi];a+=(ClimateSwingTable[li*5+zi+1]-a)*zt;b+=(ClimateSwingTable[li2*5+zi+1]-b)*zt;
 return (a+(b-a)*lt)*TEMP_SWING_SCALE*.5L;
}
[numthreads(64,1,1)] void TemperatureZones(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;if(_ClimateLand[r]==0){_ClimateTempContWrite[r]=-1;return;}
 double ld=(double)_ClimateGeo[r].x/ClimateDeg,ad=abs(ld),avg=ClimatePi*6371.0L/ClimateSqrt((double)_WorldRegionCount),area=avg*avg;
 int component=_ClimateLandComponents[r];float4 stats=_ClimateLandComponentStats[component];float2 widths=_ClimateLandComponentWidth[component];double band=ld>=0?stats.x:stats.y,above=ld>=0?stats.z:stats.w,ew=ld>=0?widths.x:widths.y;
 double zone=.25L;if(ad<=10)zone=0;else{if(ad>=35&&band>4500000.0L&&ew>2000)zone=.5L;if(zone>=.5L&&ad>=40&&above>10000000.0L)zone=.75L;if(zone>=.75L&&ad>=50)zone=1;}
 if(_ClimateWarmCoastDistance[r]>=0&&_ClimateWarmCoastDistance[r]<=max(1,(int)(150.0L/avg+.5L))&&ad<=23.5L)zone=0;
 double4 inputs=_ClimateZoneInputs[r];
 if(zone>=.5L&&inputs.x<2000)zone=min(zone,.25L);
 if(zone>=.5L&&ad>=35&&ad<=70&&inputs.y<250)zone=min(zone,.25L);
 if(ad>=30&&ad<=65){double d=inputs.z;if(d<400)zone=min(zone,.25L);else if(d<2000)zone=min(zone,.5L);else if(d<4000)zone=min(zone,.75L);}
 double componentArea=(double)_ClimateLandComponentSizes[component]*area;if(componentArea<50000)zone=0;else if(componentArea<500000)zone=min(zone,.25L);
 if(zone>=1&&inputs.w>=0){double warmth=0;for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];if(_ClimateLand[nb]==0)warmth=max(warmth,(double)_ClimateOceanSummer[nb].w);}if(inputs.w<(warmth>.3L?650.0L:150.0L))zone=.75L;}
 _ClimateTempContWrite[r]=(float)zone;
}
[numthreads(1,1,1)] void TemperaturePatchCleanup(uint ignored:SV_DispatchThreadID)
{
 // Same patch order and updated-neighbor votes as source. Membership CSR is
 // CPU planning metadata only; actual promotions read/write the GPU field.
 [loop] for(int pid=_ClimateOffset;pid<min(_ClimatePatchCount,_ClimateOffset+_ClimateCount);pid++)
 {
  int begin=_ClimatePatchOffsets[pid],end=_ClimatePatchOffsets[pid+1];if(end-begin>=(int)_ClimateParameters[7])continue;
  uint counts[5]={0,0,0,0,0};uint encountered[3]={0,0,0};uint encounterCount=0;
  for(int memberIndex=begin;memberIndex<end;memberIndex++){uint r=_ClimatePatchMembers[memberIndex];for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];if(_ClimateLand[nb]==0||_ClimatePatchLabels[nb]==pid)continue;uint zone=(uint)((double)_ClimateTempContWrite[nb]*4.0L+.5L);if(counts[zone]==0&&zone>0&&zone<4)encountered[encounterCount++]=zone;counts[zone]++;}}
  double best=_ClimatePatchZones[pid];uint maximum=0;
  // Object.entries enumerates integer-like keys 0/1 before fractional keys.
  if(counts[0]>maximum){maximum=counts[0];best=0;}
  if(counts[4]>maximum){maximum=counts[4];best=1;}
  for(uint encounterIndex=0;encounterIndex<encounterCount;encounterIndex++){uint z=encountered[encounterIndex];if(counts[z]>maximum){maximum=counts[z];best=(double)z*.25L;}}
  for(int writeIndex=begin;writeIndex<end;writeIndex++)_ClimateTempContWrite[_ClimatePatchMembers[writeIndex]]=(float)best;
 }
}
[numthreads(1,1,1)] void TemperatureZoneBuffer(uint ignored:SV_DispatchThreadID)
{
 for(uint r=(uint)_ClimateOffset;r<(uint)(_ClimateOffset+_ClimateCount);r++)
 {if(_ClimateLand[r]==0)continue;for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){uint nb=_WorldNeighbors[j];if(_ClimateLand[nb]!=0&&(double)_ClimateTempContWrite[r]-(double)_ClimateTempContWrite[nb]>.3L)_ClimateTempContWrite[r]=(float)((double)_ClimateTempContWrite[nb]+.25L);}}
}
[numthreads(64,1,1)] void TemperatureZoneOceanZero(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount&&_ClimateLand[r]==0)_ClimateTempContWrite[r]=0;}
[numthreads(64,1,1)] void TemperatureZoneFinalize(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount)_ClimateTempContWrite[r]=_ClimateLand[r]==0?-1:clamp(_ClimateTempContWrite[r],0.0,1.0);}
[numthreads(64,1,1)] void CoastalWarmthInitialize(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount)_ClimateScalarWrite[r]=_ClimateLand[r]==0?_ClimateSeasonOcean[r].w:0;}
[numthreads(64,1,1)] void CoastalWarmthDiffuse(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;double v=_ClimateScalarRead[r];if(_ClimatePlateCont[r]>=.95){_ClimateScalarWrite[r]=(float)v;return;}
 uint count=1;for(uint j=_WorldNeighborOffsets[r];j<_WorldNeighborOffsets[r+1];j++){v+=(double)_ClimateScalarRead[_WorldNeighbors[j]];count++;}_ClimateScalarWrite[r]=(float)(v/count);
}
double ClimateBaseTemperature(double distance)
{double t=max(0.0L,distance-TEMP_TROPICAL_PLATEAU_DEG)/(90.0L-TEMP_TROPICAL_PLATEAU_DEG);return TEMP_PEAK_C-TEMP_POLEWARD_RANGE_C*(double)pow((float)t,(float)TEMP_POLEWARD_EXP);}
[numthreads(64,1,1)] void TemperatureCurve(uint r:SV_DispatchThreadID)
{
 if(r>=(uint)_WorldRegionCount)return;
 double lat=_ClimateGeo[r].x,lon=_ClimateGeo[r].y,ad=abs(lat)/ClimateDeg,cont=_ClimateCont[r],pc=_ClimatePlateCont[r],elev=_WorldElevation[r],moist=_ClimateSeason==0?(double)_ClimatePrecip[r].x:(double)_ClimatePrecip[r].y;
 bool land=_ClimateLand[r]!=0,summer=_ClimateSeason==0?lat>=0:lat<0;
 double itcz=ClimateLookup(lon),ti=ClimateBaseTemperature(abs(lat-itcz)/ClimateDeg),tf=ClimateBaseTemperature(abs(lat-(_ClimateSeason==0?5.0L:-5.0L)*ClimateDeg)/ClimateDeg),blend=ClimateStep(45,90,ad),temperature=ti*(1.0L-blend)+tf*blend;
 if(land&&elev>0)temperature-=(TEMP_MOIST_LAPSE_C_PER_KM+TEMP_DRY_LAPSE_EXTRA_C_PER_KM*(1.0L-moist))*ClimateHeight(elev);
 if(!land)temperature+=(double)_ClimateSeasonOcean[r].w*min(1.0L,(double)_ClimateSeasonOcean[r].z*2.0L)*TEMP_SST_CURRENT_SHIFT_C;
 else{double cw=_ClimateScalarRead[r];if(abs(cw)>.001L)temperature+=cw*(1.0L-ClimateStep(0,.95L,pc))*TEMP_COASTAL_WARMTH_SHIFT_C;}
 if(moist>.5L)temperature*=1.0L-ClimateStep(.5L,1,moist)*TEMP_CLOUD_MOD_STRENGTH;else if(moist<.3L)temperature*=1.0L+ClimateStep(.3L,0,moist)*TEMP_CLEARSKY_AMP_STRENGTH;
 double tc=land?(double)_ClimateTempCont[r]:0,amplitude=ClimateSwing(ad,tc),sum=ClimateLookupSeason(lon,0),win=ClimateLookupSeason(lon,1),ts=ClimateBaseTemperature(abs(lat-sum)/ClimateDeg),tw=ClimateBaseTemperature(abs(lat-win)/ClimateDeg),itczAmplitude=abs(ts-tw)*.5L,extra=max(0.0L,amplitude-itczAmplitude)*TEMP_EXTRA_SWING_FACTOR;
 temperature+=summer?extra*2.0L*(1.0L-TEMP_SWING_WINTER_SHARE):-extra*2.0L*TEMP_SWING_WINTER_SHARE;
 if(land&&!summer)temperature-=cont*TEMP_CONT_WINTER_COOL_C*(1.0L-max(0.0L,(double)_ClimateWestness[r])*TEMP_WINTER_COOL_WEST_RELIEF);
 if(land)temperature+=max(0.0L,1.0L-tc*2.0L)*ClimateStep(15,50,ad)*TEMP_OCEANIC_WARMING_MAX_C;
 _ClimateScalarWrite[r]=(float)(temperature+_ClimateParameters[7]);
}
[numthreads(64,1,1)] void TemperatureNormalize(uint r:SV_DispatchThreadID)
{if(r<(uint)_WorldRegionCount){float t=(float)clamp(((double)_ClimateScalarRead[r]+45.0L)/90.0L,0.0L,1.0L);if(_ClimateSeason==0)_ClimateTemperatureWrite[r].x=t;else _ClimateTemperatureWrite[r].y=t;}}
