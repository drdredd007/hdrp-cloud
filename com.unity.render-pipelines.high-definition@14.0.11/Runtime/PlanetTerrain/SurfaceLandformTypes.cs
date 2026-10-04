using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Captured metric Hermite control. Gradient is dh/dx in the tangent plane, not a normal.</summary>
    public readonly struct SurfaceLandformControl
    {
        public readonly double3 Direction, Gradient;
        public readonly double Height, SupportMetres, VariationLimit;
        public SurfaceLandformControl(double3 direction, double height, double3 gradient, double supportMetres, double variationLimit)
        { Direction=direction; Height=height; Gradient=gradient; SupportMetres=supportMetres; VariationLimit=variationLimit; }
    }
    /// <summary>Every covering land cell participates in the captured downstream graph, including headwaters.</summary>
    public readonly struct SurfaceLandformChannel
    {
        public readonly int Control, Parent, Outlet, Strahler;
        public readonly double Area, RiseScale;
        public SurfaceLandformChannel(int control,int parent,int outlet,int strahler,double area,double riseScale)
        {Control=control;Parent=parent;Outlet=outlet;Strahler=strahler;Area=area;RiseScale=riseScale;}
    }
    /// <summary>A shared watershed control between two covering cells; its captured height participates in Full sampling.</summary>
    public readonly struct SurfaceLandformDivide
    {
        public readonly int Control, FirstChannel, SecondChannel, Flags;
        public SurfaceLandformDivide(int control,int firstChannel,int secondChannel,int flags)
        {Control=control;FirstChannel=firstChannel;SecondChannel=secondChannel;Flags=flags;}
    }
    /// <summary>A complete adaptive sphere partition, independent of renderer patches. References index Divides.</summary>
    public readonly struct SurfaceLandformCell
    {
        public readonly SurfaceTileKey Key;
        public readonly int Channel, FirstDivide, DivideCount;
        public SurfaceLandformCell(SurfaceTileKey key,int channel,int firstDivide,int divideCount)
        {Key=key;Channel=channel;FirstDivide=firstDivide;DivideCount=divideCount;}
    }
}
