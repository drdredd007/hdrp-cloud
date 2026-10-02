using SpaceRunner.PlanetTerrain;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Snapshot-aware surface adapter. Overloads taking a NativeSurfaceView are safe for Burst jobs.</summary>
    public static class PlanetSurfaceData
    {
        public const double NormalSampleMetres=1;
        public static double NormalSampleMetresFor(double radius)=>math.min(NormalSampleMetres,radius*.125);
        static bool Valid(in PlanetDefinition definition,double3 direction)=>definition.IsValid && math.all(math.isfinite(direction)) && math.cmax(math.abs(direction))>0;
        public static SurfaceSampleStatus TryHeight(in PlanetDefinition definition,double3 direction,out double height)
        {
            height=0;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3){height=PlanetField.Height(definition,direction);return SurfaceSampleStatus.Ready;}
            if(!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out var lease))return SurfaceSampleStatus.NotReady;
            using(lease)return TryHeight(definition,lease.View,direction,out height);
        }
        public static SurfaceSampleStatus TryHeight(in PlanetDefinition definition,in NativeSurfaceView view,double3 direction,out double height)
        {
            height=0;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3){height=PlanetField.Height(definition,direction);return SurfaceSampleStatus.Ready;}
            if(!Compatible(definition,view))return SurfaceSampleStatus.IncompatibleData;
            return SurfaceSampler.TrySampleHeight(view,direction,out height);
        }
        public static SurfaceSampleStatus TrySurface(in PlanetDefinition definition,double3 direction,out double3 surface)
        {
            surface=default;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3){surface=PlanetField.Surface(definition,direction);return SurfaceSampleStatus.Ready;}
            if(!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out var lease))return SurfaceSampleStatus.NotReady;
            using(lease)return TrySurface(definition,lease.View,direction,out surface);
        }
        public static SurfaceSampleStatus TrySurface(in PlanetDefinition definition,in NativeSurfaceView view,double3 direction,out double3 surface)
        {
            surface=default;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3){surface=PlanetField.Surface(definition,direction);return SurfaceSampleStatus.Ready;}
            var status=TryHeight(definition,view,direction,out var height);if(status!=SurfaceSampleStatus.Ready)return status;
            if(!CubeSurface.TryNormalize(direction,out var radial))return SurfaceSampleStatus.InvalidInput;
            surface=radial*(definition.Radius+height);
            return math.all(math.isfinite(surface)) && definition.Radius+height>0?SurfaceSampleStatus.Ready:SurfaceSampleStatus.IncompatibleData;
        }
        public static SurfaceSampleStatus TryNormal(in PlanetDefinition definition,double3 direction,out float3 normal)
        {
            normal=default;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3){normal=PlanetField.Normal(definition,direction);return SurfaceSampleStatus.Ready;}
            if(!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out var lease))return SurfaceSampleStatus.NotReady;
            using(lease)return TryNormal(definition,lease.View,direction,out normal);
        }
        public static SurfaceSampleStatus TryNormal(in PlanetDefinition definition,in NativeSurfaceView view,double3 direction,out float3 normal)
        {
            normal=default;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3){normal=PlanetField.Normal(definition,direction);return SurfaceSampleStatus.Ready;}
            if(!Compatible(definition,view))return SurfaceSampleStatus.IncompatibleData;
            var status=SurfaceSampler.TrySampleNormal(view,direction,NormalSampleMetresFor(definition.Radius),out var precise);normal=(float3)precise;return status;
        }
        public static float4 Color(in PlanetDefinition definition,double3 direction)
            => definition.GeneratorVersion==3?new float4(.24f,.22f,.19f,1):PlanetField.Color(definition,direction);
        public static SurfaceSampleStatus TryAttributes(in PlanetDefinition definition,double3 direction,out SurfaceAttributes attributes,SurfaceChannels required=SurfaceChannels.None)
        {
            attributes=default;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3)return SurfaceSampleStatus.NotReady;
            if(!PlanetSurfaceDataRegistry.TryAcquire(definition.Surface,out var lease))return SurfaceSampleStatus.NotReady;
            using(lease)return TryAttributes(definition,lease.View,direction,out attributes,required);
        }
        public static SurfaceSampleStatus TryAttributes(in PlanetDefinition definition,in NativeSurfaceView view,double3 direction,out SurfaceAttributes attributes,SurfaceChannels required=SurfaceChannels.None)
        {
            attributes=default;if(!Valid(definition,direction))return SurfaceSampleStatus.InvalidInput;
            if(definition.GeneratorVersion!=3)return SurfaceSampleStatus.NotReady;
            if(!Compatible(definition,view))return SurfaceSampleStatus.IncompatibleData;
            return SurfaceSampler.TrySampleAttributes(view,direction,out attributes,required);
        }
        public static bool Compatible(in PlanetDefinition definition,in NativeSurfaceView view)
            => view.Recipe.IsValid && definition.Radius==view.Recipe.Radius && definition.Seed==view.Recipe.Seed &&
                definition.Relief>=math.max(math.abs(view.MinimumHeight),math.abs(view.MaximumHeight)) && definition.Surface.Equals(PlanetSurfaceDescriptor.FromView(view));
    }
}
