using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Inclusive XY rectangles, one per cube face. Invalid faces have X1/Y1 = -1. No managed allocation enters a job.</summary>
    public struct SurfaceScatterSpacingNeighborhood
    {
        public int4 Face0,Face1,Face2,Face3,Face4,Face5;
        public int CellCount;
        public int4 Rectangle(int face)
        {switch(face){case 0:return Face0;case 1:return Face1;case 2:return Face2;case 3:return Face3;case 4:return Face4;default:return Face5;}}
        internal void Set(int face,int4 rectangle)
        {switch(face){case 0:Face0=rectangle;break;case 1:Face1=rectangle;break;case 2:Face2=rectangle;break;case 3:Face3=rectangle;break;case 4:Face4=rectangle;break;default:Face5=rectangle;break;}}
    }
    /// <summary>Same-species priority thinning of density-admitted raw proposals. The minimum is a reference-sphere chord,
    /// not terrain-geodesic distance, mesh clearance or a cross-species exclusion. Habitat/exclusion gates may underfill.</summary>
    public static class SurfaceScatterSpacing
    {
        public const int MaximumNeighborCells=64,MaximumNeighborProposals=8192;
        // Guard covers unit-vector normalization/arithmetic uncertainty; it is 14.2 mm at R=1e12 m.
        public const double NumericalPadding=1.42108547152020037174224853515625e-14;
        public static SurfaceSampleStatus Validate(in SurfaceScatterSpecies species,double radius,out int maximumProposals)
        {
            maximumProposals=0;
            if(!species.IsValid||!math.isfinite(radius)||radius<=0||!math.isfinite(radius*radius))return SurfaceSampleStatus.InvalidInput;
            if(species.MinimumReferenceChordSpacingMetres==0)return SurfaceSampleStatus.Ready;
            double c=species.MinimumReferenceChordSpacingMetres/radius+2*NumericalPadding;
            if(!math.isfinite(c)||c>=.125)return SurfaceSampleStatus.InvalidInput;
            // Every participating dominant face has d >= 1/sqrt(3)-2c. Rational cap bounds span
            // at most 2c/d+2c(1+c)/d² in either face coordinate; at c<1/8 no opposite face pair participates.
            double d=1/Math.Sqrt(3)-2*c,span=2*c/d+2*c*(1+c)/(d*d),grid=1L<<species.FixedLevel;
            double axis=math.min(grid,math.ceil(span*grid*.5)+2),cells=3*axis*axis;
            if(cells>MaximumNeighborCells||cells*species.CandidatesPerCell>MaximumNeighborProposals)return SurfaceSampleStatus.InvalidInput;
            maximumProposals=(int)cells*species.CandidatesPerCell;return SurfaceSampleStatus.Ready;
        }
        public static SurfaceSampleStatus TryDescribe(in SurfaceScatterSpecies species,double radius,double3 direction,out SurfaceScatterSpacingNeighborhood neighborhood)
        {
            neighborhood=default;
            if(Validate(species,radius,out _)!=SurfaceSampleStatus.Ready||!CubeSurface.TryNormalize(direction,out var unit))return SurfaceSampleStatus.InvalidInput;
            var absent=new int4(0,0,-1,-1);for(int face=0;face<6;face++)neighborhood.Set(face,absent);
            if(species.MinimumReferenceChordSpacingMetres==0)return SurfaceSampleStatus.Ready;
            double chord=species.MinimumReferenceChordSpacingMetres/radius+2*NumericalPadding;
            var lo=math.max(new double3(-1),unit-chord);var hi=math.min(new double3(1),unit+chord);int grid=(int)(1L<<species.FixedLevel);
            for(int face=0;face<6;face++)
            {
                SurfaceScatterCells.FaceBounds(face,lo,hi,out double d0,out double d1,out double2 n0,out double2 n1,out double other);
                if(d1<=0||d1<other)continue;d0=math.max(d0,other);double2 a0,a1;
                if(d0<=0){a0=new double2(-1);a1=new double2(1);}
                else
                {
                    var p00=n0/d0;var p01=n0/d1;var p10=n1/d0;var p11=n1/d1;
                    a0=math.max(new double2(-1),math.min(math.min(p00,p01),math.min(p10,p11)));
                    a1=math.min(new double2(1),math.max(math.max(p00,p01),math.max(p10,p11)));
                }
                if(math.any(a0>a1))continue;
                var lower=(int2)math.min(grid-1,math.floor(math.max(new double2(0),(a0+1)*.5)*grid));
                var upper=(int2)math.min(grid-1,math.floor(math.min(new double2(1),(a1+1)*.5)*grid));
                long count=((long)upper.x-lower.x+1)*((long)upper.y-lower.y+1);
                if(count+neighborhood.CellCount>MaximumNeighborCells||(count+neighborhood.CellCount)*species.CandidatesPerCell>MaximumNeighborProposals)
                {neighborhood=default;return SurfaceSampleStatus.InvalidInput;}
                neighborhood.Set(face,new int4(lower.x,lower.y,upper.x,upper.y));neighborhood.CellCount+=(int)count;
            }
            return SurfaceSampleStatus.Ready;
        }
        public static bool RawDensityAdmitted(in SurfaceScatterSpecies species,in SurfaceScatterKey key,double radius,double2 cubeCoordinates)
        {
            double width=2.0/(1L<<species.FixedLevel),shape=1+cubeCoordinates.x*cubeCoordinates.x+cubeCoordinates.y*cubeCoordinates.y;
            // Match the existing proposal probability, excluding only material affinity.
            double jacobian=radius*radius/math.pow(shape,1.5);
            double probability=species.DensityPerSquareMetre*width*width*jacobian/species.CandidatesPerCell;
            return SurfaceScatterRandom.Open01(SurfaceScatterRandom.Word(key,species.Seed,2))<probability;
        }
        public static bool HasLowerPriority(in SurfaceScatterKey a,in SurfaceScatterKey b,uint seed)
        {
            uint x=SurfaceScatterRandom.Word(a,seed,5),y=SurfaceScatterRandom.Word(b,seed,5);
            return x<y||(x==y&&a.CompareTo(b)<0);
        }
        public static SurfaceSampleStatus TryRetain(in SurfaceScatterSpecies species,in SurfaceScatterKey key,double radius,double3 direction,out bool retained)
        {
            retained=false;
            if(!species.IsValid||!key.IsValid||key.SpeciesId!=species.SpeciesId||key.Cell.Level!=species.FixedLevel||key.Slot>=species.CandidatesPerCell)
                return SurfaceSampleStatus.InvalidInput;
            if(Validate(species,radius,out _)!=SurfaceSampleStatus.Ready || !math.all(math.isfinite(direction)) || math.abs(math.lengthsq(direction)-1)>1e-12)
                return SurfaceSampleStatus.InvalidInput;
            if(species.MinimumReferenceChordSpacingMetres==0){retained=true;return SurfaceSampleStatus.Ready;}
            var status=TryDescribe(species,radius,direction,out var neighborhood);if(status!=SurfaceSampleStatus.Ready)return status;
            double threshold=species.MinimumReferenceChordSpacingMetres/radius+NumericalPadding,thresholdSquared=threshold*threshold;
            for(int face=0;face<6;face++)
            {
                var rectangle=neighborhood.Rectangle(face);
                for(int y=rectangle.y;y<=rectangle.w;y++)for(int x=rectangle.x;x<=rectangle.z;x++)for(int slot=0;slot<species.CandidatesPerCell;slot++)
                {
                    var other=new SurfaceScatterKey(key.Planet,key.SpeciesId,new SurfaceTileKey(face,species.FixedLevel,x,y),slot);
                    if(other.Equals(key)||!HasLowerPriority(other,key,species.Seed))continue;
                    if(SurfaceScatterSampler.TryDirection(other,species.Seed,out var proposed,out var coordinates)!=SurfaceSampleStatus.Ready)return SurfaceSampleStatus.InvalidInput;
                    if(!RawDensityAdmitted(species,other,radius,coordinates))continue;
                    var delta=direction-proposed;double distanceSquared=(delta.x*delta.x+delta.y*delta.y)+delta.z*delta.z;
                    if(distanceSquared<=thresholdSquared)return SurfaceSampleStatus.Ready;
                }
            }
            retained=true;return SurfaceSampleStatus.Ready;
        }
    }
}
