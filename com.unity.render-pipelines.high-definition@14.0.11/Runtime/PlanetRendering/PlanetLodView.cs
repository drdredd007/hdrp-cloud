using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Planet-local, unjittered visual interest. It changes refinement priority, never the complete planet cover.</summary>
    public readonly struct PlanetLodView : IEquatable<PlanetLodView>
    {
        public readonly double4 Rotation;
        public readonly float FieldOfView;
        public readonly double Aspect,PaddingMetres,AngularPaddingDegrees;
        // Projection m02/m12 in Unity's unjittered perspective convention, before GPU Y inversion.
        public readonly double2 ProjectionShift;
        public PlanetLodView(quaternion localRotation,float fieldOfView,double aspect,double paddingMetres=0,
            double angularPaddingDegrees=1,double2 projectionShift=default)
        {
            var q=(double4)localRotation.value;
            Rotation=math.all(math.isfinite(q))&&math.lengthsq(q)>0?math.normalize(q):q;
            FieldOfView=fieldOfView;Aspect=aspect;PaddingMetres=paddingMetres;
            AngularPaddingDegrees=angularPaddingDegrees;ProjectionShift=projectionShift;
        }
        public bool IsValid=>math.all(math.isfinite(Rotation))&&math.abs(math.lengthsq(Rotation)-1)<1e-10&&
            math.isfinite(FieldOfView)&&FieldOfView>0&&FieldOfView<180&&math.isfinite(Aspect)&&Aspect>0&&
            math.isfinite(PaddingMetres)&&PaddingMetres>=0&&math.isfinite(AngularPaddingDegrees)&&
            AngularPaddingDegrees>=0&&AngularPaddingDegrees<45&&math.all(math.isfinite(ProjectionShift));

        public static bool TryFromProjection(quaternion localRotation,Matrix4x4 projection,double paddingMetres,out PlanetLodView view)
        {
            view=default;
            // HDRP adds jitter to its own matrix. Camera.projectionMatrix retains the authored perspective,
            // including physical-camera lens shift. Unknown skew/oblique XY mappings use full-cover fallback.
            if(!math.isfinite(projection.m00)||!math.isfinite(projection.m11)||projection.m00<=0||projection.m11<=0||
                projection.m01!=0||projection.m10!=0||projection.m03!=0||projection.m13!=0||
                projection.m30!=0||projection.m31!=0||projection.m32!=-1||projection.m33!=0)return false;
            float fov=(float)math.degrees(2*math.atan(1/(double)projection.m11));
            view=new PlanetLodView(localRotation,fov,(double)projection.m11/projection.m00,paddingMetres,1,
                new double2(projection.m02,projection.m12));
            return view.IsValid;
        }

        /// <summary>The camera's native near/far clipping distances do not bound the scaled celestial layer.</summary>
        public bool Intersects(in PlanetDefinition definition,PlanetPatchKey key,double3 camera)
            =>IntersectsRadial(definition,key,camera,math.max(0,definition.Radius-definition.Relief),definition.Radius+definition.Relief);
        public bool Intersects(in PlanetDefinition definition,PlanetPatchKey key,double3 camera,
            double minimumRenderedHeight,double maximumRenderedHeight)
        {
            if(!math.isfinite(minimumRenderedHeight)||!math.isfinite(maximumRenderedHeight)||minimumRenderedHeight>maximumRenderedHeight||
                !(definition.Radius+minimumRenderedHeight>0))return Intersects(definition,key,camera);
            return IntersectsRadial(definition,key,camera,definition.Radius+minimumRenderedHeight,definition.Radius+maximumRenderedHeight);
        }
        /// <summary>Supports the radial cone's metric Minkowski ball, including transverse float packing error.</summary>
        public bool Intersects(in PlanetDefinition definition,PlanetPatchKey key,double3 camera,
            double minimumRenderedHeight,double maximumRenderedHeight,double positionReserveMetres)
        {
            if(!math.isfinite(positionReserveMetres)||positionReserveMetres<0)return true;
            if(!math.isfinite(minimumRenderedHeight)||!math.isfinite(maximumRenderedHeight)||minimumRenderedHeight>maximumRenderedHeight||
                !(definition.Radius+minimumRenderedHeight>0))
                return IntersectsRadial(definition,key,camera,math.max(0,definition.Radius-definition.Relief),definition.Radius+definition.Relief,positionReserveMetres);
            return IntersectsRadial(definition,key,camera,definition.Radius+minimumRenderedHeight,definition.Radius+maximumRenderedHeight,positionReserveMetres);
        }
        bool IntersectsRadial(in PlanetDefinition definition,PlanetPatchKey key,double3 camera,double radialMin,double radialMax,double positionReserveMetres=0)
        {
            // An invalid bound can never prove invisibility. The selector rejects invalid views separately.
            if(!IsValid||!definition.IsValid||!math.all(math.isfinite(camera))||key.Face<0||key.Face>=6||
                key.Level<0||key.Level>PlanetLodSelector.MaximumSupportedLevel||key.X<0||key.Y<0||
                key.X>=(1<<key.Level)||key.Y>=(1<<key.Level))return true;
            var center=PlanetField.Direction(key,.5,.5);
            double angle=2*math.asin(math.min(1,Math.Sqrt(2)/(2*(1L<<key.Level))));
            double cosine=math.cos(angle),sine=math.sin(angle);
            var right=PlanetField.Rotate(Rotation,new double3(1,0,0));
            var up=PlanetField.Rotate(Rotation,new double3(0,1,0));
            var forward=PlanetField.Rotate(Rotation,new double3(0,0,1));
            // A metric margin preserves the existing receiver/shadow neighbourhood during native-bank transitions.
            double margin=PaddingMetres+positionReserveMetres+64*2.2204460492503131e-16*(radialMax+math.length(camera)+1);
            if(Outside(forward,center,camera,radialMin,radialMax,cosine,sine,margin))return false;
            double tangent=math.tan(math.radians((double)FieldOfView)*.5),pad=math.radians(AngularPaddingDegrees);
            return !Outside(Side(right,forward,tangent*Aspect*(1-ProjectionShift.x),pad),center,camera,radialMin,radialMax,cosine,sine,margin)&&
                !Outside(Side(-right,forward,tangent*Aspect*(1+ProjectionShift.x),pad),center,camera,radialMin,radialMax,cosine,sine,margin)&&
                !Outside(Side(up,forward,tangent*(1-ProjectionShift.y),pad),center,camera,radialMin,radialMax,cosine,sine,margin)&&
                !Outside(Side(-up,forward,tangent*(1+ProjectionShift.y),pad),center,camera,radialMin,radialMax,cosine,sine,margin);
        }
        static double3 Side(double3 axis,double3 forward,double tangent,double padding)
        {
            double angle=math.min(Math.PI*.5-1e-10,math.atan(tangent)+padding);
            return axis*math.cos(angle)+forward*math.sin(angle);
        }
        static bool Outside(double3 plane,double3 center,double3 camera,double minimum,double maximum,
            double cosine,double sine,double margin)
        {
            double q=math.clamp(math.dot(center,plane),-1,1);
            // Support of the ENTIRE angular cone, not a finite corner test. Multiplication by the radial
            // interval keeps height uncertainty radial instead of inflating all directions by global relief.
            double support=q>=cosine?1:q*cosine+math.sqrt(math.max(0,1-q*q))*sine;
            double farthest=(support>=0?maximum:minimum)*support;
            return farthest-math.dot(camera,plane)+margin<0;
        }
        public bool Equals(PlanetLodView other)=>(Rotation.Equals(other.Rotation)||Rotation.Equals(-other.Rotation))&&
            FieldOfView.Equals(other.FieldOfView)&&Aspect.Equals(other.Aspect)&&PaddingMetres.Equals(other.PaddingMetres)&&
            AngularPaddingDegrees.Equals(other.AngularPaddingDegrees)&&ProjectionShift.Equals(other.ProjectionShift);
        public override bool Equals(object other)=>other is PlanetLodView view&&Equals(view);
        public override int GetHashCode()=>math.abs(Rotation).GetHashCode()^FieldOfView.GetHashCode()^Aspect.GetHashCode();
    }
}
