using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Version-one SM5 portable unsigned arithmetic. No floating pose or surface revision enters the key stream.</summary>
    public static class SurfaceScatterRandom
    {
        public static uint Mix(uint value)
        {unchecked{value^=value>>16;value*=0x7feb352du;value^=value>>15;value*=0x846ca68bu;value^=value>>16;return value;}}
        public static uint Fold(in SurfaceScatterKey key)
        {
            unchecked
            {
            uint state=0x6d2b79f5u;
            state=Mix(state^(uint)key.Planet.High);state=Mix(state^(uint)(key.Planet.High>>32));state=Mix(state^(uint)key.Planet.Low);state=Mix(state^(uint)(key.Planet.Low>>32));
            state=Mix(state^key.SpeciesId);state=Mix(state^(uint)key.Cell.Face);state=Mix(state^(uint)key.Cell.Level);state=Mix(state^(uint)key.Cell.X);state=Mix(state^(uint)key.Cell.Y);return Mix(state^(uint)key.Slot);
            }
        }
        public static uint Word(in SurfaceScatterKey key,uint seed,uint stream)=>unchecked(Mix(Fold(key)^Mix(seed+0x9e3779b9u)^(stream*0x85ebca6bu)));
        public static double Open01(uint word)=>((double)word+.5)/4294967296.0;
    }
    public static class SurfaceScatterSampler
    {
        const double InteriorEpsilon=8.8817841970012523233890533447265625e-16; // 2^-50, including level30 face/cell boundaries.
        public static SurfaceSampleStatus TryDirection(in SurfaceScatterKey key,uint seed,out double3 direction,out double2 cubeCoordinates)
        {
            direction=default;cubeCoordinates=default;if(!key.IsValid)return SurfaceSampleStatus.InvalidInput;
            double count=1L<<key.Cell.Level;var cell=new double2(key.Cell.X,key.Cell.Y);double2 lower=2*cell/count-1,upper=2*(cell+1)/count-1;
            var jitter=new double2(SurfaceScatterRandom.Open01(SurfaceScatterRandom.Word(key,seed,0)),SurfaceScatterRandom.Open01(SurfaceScatterRandom.Word(key,seed,1)));
            cubeCoordinates=math.clamp(2*(cell+jitter)/count-1,lower+InteriorEpsilon,upper-InteriorEpsilon);
            // Global face parameters use the same X,Y,Z tie and axes as CubeSurface.
            double a=cubeCoordinates.x,b=cubeCoordinates.y;double3 cube;
            switch(key.Cell.Face){case 0:cube=new double3(1,b,-a);break;case 1:cube=new double3(-1,b,a);break;case 2:cube=new double3(a,1,-b);break;case 3:cube=new double3(a,-1,b);break;case 4:cube=new double3(a,b,1);break;default:cube=new double3(-a,b,-1);break;}
            return CubeSurface.TryNormalize(cube,out direction)?SurfaceSampleStatus.Ready:SurfaceSampleStatus.InvalidInput;
        }
        /// <summary>Reference-sphere cell area: exact solid-angle integral through level12; stable midpoint Jacobian for smaller cells.</summary>
        public static double CellArea(SurfaceTileKey cell,double radius)
        {
            if(!cell.IsValid||!math.isfinite(radius)||radius<=0||!math.isfinite(radius*radius))return double.NaN;
            double count=1L<<cell.Level,a0=2*cell.X/count-1,a1=2*(cell.X+1.0)/count-1,b0=2*cell.Y/count-1,b1=2*(cell.Y+1.0)/count-1;
            // Avoid subtracting almost equal antiderivatives for tiny level30 cells.
            if(cell.Level>12){double a=(a0+a1)*.5,b=(b0+b1)*.5,w=2/count;return radius*radius*w*w/math.pow(1+a*a+b*b,1.5);}
            return radius*radius*(Solid(a1,b1)-Solid(a0,b1)-Solid(a1,b0)+Solid(a0,b0));
        }
        static double Solid(double a,double b)=>math.atan2(a*b,math.sqrt(1+a*a+b*b));
        public static SurfaceSampleStatus TryCandidate(in SurfaceScatterSurfaceView source,in SurfaceScatterSpecies species,SurfaceScatterPlanetId planet,
            SurfaceTileKey cell,int slot,in NativeSurfaceScatterExclusionsView exclusions,out SurfaceScatterCandidate candidate,out SurfaceScatterDecision decision)
        {
            candidate=default;decision=SurfaceScatterDecision.NotEvaluated;var view=source.Surface;
            if(!species.IsValid || !planet.IsValid || !cell.IsValid || cell.Level!=species.FixedLevel || slot<0 || slot>=species.CandidatesPerCell || !view.Recipe.IsValid || !view.Revision.IsValid)
                return SurfaceSampleStatus.InvalidInput;
            if(exclusions.IsPresent && (!exclusions.Planet.Equals(planet)||exclusions.Epoch==0))return SurfaceSampleStatus.InvalidInput;
            if(!source.MaterialsReady || (exclusions.IsPresent&&!exclusions.IsReady))return SurfaceSampleStatus.NotReady;
            double radius=view.Recipe.Radius,count=1L<<cell.Level,width=2/count;
            if(!math.isfinite(radius*radius))return SurfaceSampleStatus.InvalidInput;
            if(species.MinimumReferenceChordSpacingMetres>0 && SurfaceScatterSpacing.Validate(species,radius,out _)!=SurfaceSampleStatus.Ready)
                return SurfaceSampleStatus.InvalidInput;
            double2 low=2*new double2(cell.X,cell.Y)/count-1,high=low+width;
            var closest=math.clamp(new double2(0),low,high);
            double capacity=species.DensityPerSquareMetre*radius*radius*width*width/math.pow(1+math.lengthsq(closest),1.5);
            if(!math.isfinite(capacity)||capacity>species.CandidatesPerCell)return SurfaceSampleStatus.InvalidInput;
            var key=new SurfaceScatterKey(planet,species.SpeciesId,cell,slot);
            var status=TryDirection(key,species.Seed,out var direction,out var coordinates);if(status!=SurfaceSampleStatus.Ready)return status;
            status=SurfaceSampler.TrySampleHeight(view,direction,out var height);if(status!=SurfaceSampleStatus.Ready)return status;
            status=SurfaceSampler.TrySampleNormal(view,direction,species.NormalSampleMetres,out var normal);if(status!=SurfaceSampleStatus.Ready)return status;
            var required=species.RequiredChannels|SurfaceChannels.MaterialWeights;
            if(species.MinimumWetness>0||species.MaximumWetness<1)required|=SurfaceChannels.ErosionData;
            status=SurfaceSampler.TrySampleAttributes(view,direction,out var attributes,required);if(status!=SurfaceSampleStatus.Ready)return status;
            if(!math.all(math.isfinite(attributes.MaterialWeights))||math.csum(attributes.MaterialWeights)<=0||
                ((attributes.Channels&SurfaceChannels.ErosionData)!=0&&!math.all(math.isfinite(attributes.ErosionData))))return SurfaceSampleStatus.IncompatibleData;
            double alignment=math.clamp(math.dot(direction,normal),-1,1),slope=math.acos(alignment)*180/Math.PI;
            double yaw=SurfaceScatterRandom.Open01(SurfaceScatterRandom.Word(key,species.Seed,3))*2*Math.PI;
            double3 reference=math.abs(normal.y)<.9?new double3(0,1,0):new double3(1,0,0);
            var forward=math.normalize(reference-normal*math.dot(reference,normal));var right=math.cross(normal,forward);
            var rotated=forward*math.cos(yaw)+right*math.sin(yaw);var rotation=quaternion.LookRotationSafe((float3)rotated,(float3)normal);
            float scale=(float)math.lerp((double)species.ScaleRange.x,species.ScaleRange.y,SurfaceScatterRandom.Open01(SurfaceScatterRandom.Word(key,species.Seed,4)));
            var position=direction*(radius+height);
            if(!math.all(math.isfinite(position))||radius+height<=0||!math.all(math.isfinite(rotation.value)))return SurfaceSampleStatus.IncompatibleData;
            status=SurfaceScatterExclusionSampler.TryExcluded(exclusions,key,direction,radius,out bool excluded);if(status!=SurfaceSampleStatus.Ready)return status;
            if(species.ExcludeSurfaceStamps)for(int i=0;i<view.Stamps.Length;i++)
            {
                var stamp=view.Stamps[i];if(!stamp.IsValid)return SurfaceSampleStatus.IncompatibleData;
                if(SurfaceScatterExclusionSampler.ArcMetres(direction,stamp.CenterDirection,radius)<=stamp.RadiusMetres+stamp.RimWidthMetres)excluded=true;
            }
            candidate=new SurfaceScatterCandidate(key,direction,position,normal,rotation,scale,height,slope,yaw,attributes);
            if(excluded){decision=SurfaceScatterDecision.Excluded;return SurfaceSampleStatus.Ready;}
            if(height<species.MinimumHeight || height>species.MaximumHeight){decision=SurfaceScatterDecision.Height;return SurfaceSampleStatus.Ready;}
            if(alignment<math.cos(species.MaximumSlopeDegrees*Math.PI/180)){decision=SurfaceScatterDecision.Slope;return SurfaceSampleStatus.Ready;}
            if((attributes.Channels&SurfaceChannels.ErosionData)!=0 && (attributes.ErosionData.y<species.MinimumWetness || attributes.ErosionData.y>species.MaximumWetness))
            {decision=SurfaceScatterDecision.Wetness;return SurfaceSampleStatus.Ready;}
            double affinity=math.dot(attributes.MaterialWeights,species.MaterialAffinity);
            if(!(affinity>0)){decision=SurfaceScatterDecision.Material;return SurfaceSampleStatus.Ready;}
            double jacobian=radius*radius/math.pow(1+math.lengthsq(coordinates),1.5);
            double probability=species.DensityPerSquareMetre*width*width*jacobian/species.CandidatesPerCell*affinity;
            decision=SurfaceScatterRandom.Open01(SurfaceScatterRandom.Word(key,species.Seed,2))<probability?SurfaceScatterDecision.Accepted:SurfaceScatterDecision.Density;
            if(decision==SurfaceScatterDecision.Accepted && species.MinimumReferenceChordSpacingMetres>0)
            {
                status=SurfaceScatterSpacing.TryRetain(species,key,radius,direction,out bool retained);
                if(status!=SurfaceSampleStatus.Ready){decision=SurfaceScatterDecision.NotEvaluated;return status;}
                if(!retained)decision=SurfaceScatterDecision.Spacing;
            }
            return SurfaceSampleStatus.Ready;
        }
    }
}
