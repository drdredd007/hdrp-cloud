using System;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Terrain classification, independent of colour, camera, chart and scatter species.</summary>
    [Serializable]
    public struct SurfaceRockyPaintRules
    {
        public float SlopeStartDegrees, SlopeEndDegrees;
        public float SedimentCoverage, SlopeExposure, RidgeExposure, HollowAccumulation;
        public float CurvatureReferencePerMetre, BoundaryVariation;
        public double BoundaryWavelengthMetres;
        public int Seed;
        public static SurfaceRockyPaintRules Default => new SurfaceRockyPaintRules {
            SlopeStartDegrees=12, SlopeEndDegrees=45, SedimentCoverage=.75f, SlopeExposure=.65f,
            RidgeExposure=.45f, HollowAccumulation=.4f, CurvatureReferencePerMetre=.002f,
            BoundaryVariation=.12f, BoundaryWavelengthMetres=256, Seed=8123
        };
        public bool IsValid => math.all(math.isfinite(new float4(SlopeStartDegrees,SlopeEndDegrees,SedimentCoverage,SlopeExposure))) &&
            math.all(math.isfinite(new float4(RidgeExposure,HollowAccumulation,CurvatureReferencePerMetre,BoundaryVariation))) &&
            SlopeStartDegrees>=0 && SlopeEndDegrees>SlopeStartDegrees && SlopeEndDegrees<=90 &&
            SedimentCoverage>=0 && SedimentCoverage<=1 && SlopeExposure>=0 && SlopeExposure<=1 &&
            RidgeExposure>=0 && RidgeExposure<=1 && HollowAccumulation>=0 && HollowAccumulation<=1 &&
            CurvatureReferencePerMetre>0 && BoundaryVariation>=0 && BoundaryVariation<=1 &&
            math.isfinite(BoundaryWavelengthMetres) && BoundaryWavelengthMetres>=1;

        /// <param name="curvaturePerMetre">Signed radial-height Laplacian: positive in hollows, negative on ridges.</param>
        public bool TryEvaluate(double3 unitDirection,double radius,double slopeDegrees,double curvaturePerMetre,
            out float4 weights,out float4 features)
        {
            weights=features=default;
            if(!IsValid || !math.isfinite(slopeDegrees) || slopeDegrees<0 || slopeDegrees>90 || !math.isfinite(curvaturePerMetre)) return false;
            if(!CubeSurface.TryNormalize(unitDirection,out var unit)||!math.isfinite(radius)||radius<=0) return false;
            double3 p=unit*(radius/BoundaryWavelengthMetres);
            if(math.any(math.abs(p)>4503599627370494))return false;
            double variation=Noise(p,unchecked((uint)Seed));
            double slope=math.smoothstep((double)SlopeStartDegrees,SlopeEndDegrees,slopeDegrees);
            double signed=math.clamp(curvaturePerMetre/CurvatureReferencePerMetre,-1,1);
            double ridge=math.max(0,-signed),hollow=math.max(0,signed);
            double sediment=math.saturate(SedimentCoverage-SlopeExposure*slope-RidgeExposure*ridge+
                HollowAccumulation*hollow+BoundaryVariation*variation);
            // Canonical order retained for material and scatter consumers: grass, sand, rock, snow.
            weights=new float4(0,(float)sediment,1-(float)sediment,0);
            features=new float4((float)slope,(float)ridge,(float)hollow,(float)(variation*.5+.5));
            return true;
        }
        static uint Hash(double3 cell,uint seed)
        {
            var wrapped=cell-math.floor(cell/65536)*65536;
            unchecked {uint h=(uint)wrapped.x*73856093u^(uint)wrapped.y*19349663u^(uint)wrapped.z*83492791u^seed;
                h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;return h^(h>>16);}
        }
        public static double Noise(double3 coordinate,uint seed)
        {
            var cell=math.floor(coordinate);var t=coordinate-cell;t=t*t*t*(t*(t*6-15)+10);double value=0;
            for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++) {
                double weight=(x==0?1-t.x:t.x)*(y==0?1-t.y:t.y)*(z==0?1-t.z:t.z);
                value+=((Hash(cell+new double3(x,y,z),seed)&65535)/32767.5-1)*weight;
            }
            return value;
        }
    }
}
