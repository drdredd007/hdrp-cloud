using System;
using Unity.Mathematics;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Value-only surface identity. A zero descriptor retains the existing noise generator.</summary>
    [Serializable]
    public struct PlanetSurfaceDescriptor : IEquatable<PlanetSurfaceDescriptor>
    {
        public uint4 RecipeDigestA,RecipeDigestB,BaseDigestA,BaseDigestB;
        public uint4 ContentDigestA,ContentDigestB;
        public ulong Epoch;
        public bool IsBound => Epoch!=0 && (math.any(BaseDigestA!=0) || math.any(BaseDigestB!=0)) && (math.any(ContentDigestA!=0) || math.any(ContentDigestB!=0));
        static uint4 Low(SurfaceContentHash h) => new uint4((uint)h.A,(uint)(h.A>>32),(uint)h.B,(uint)(h.B>>32));
        static uint4 High(SurfaceContentHash h) => new uint4((uint)h.C,(uint)(h.C>>32),(uint)h.D,(uint)(h.D>>32));
        public static PlanetSurfaceDescriptor FromRevision(SurfaceRevision revision) => new PlanetSurfaceDescriptor
        {RecipeDigestA=Low(revision.RecipeDigest),RecipeDigestB=High(revision.RecipeDigest),BaseDigestA=Low(revision.BaseDigest),BaseDigestB=High(revision.BaseDigest),Epoch=revision.Epoch};
        public static PlanetSurfaceDescriptor FromSnapshot(SurfaceSnapshot snapshot)
        {var value=FromRevision(snapshot.Revision);value.ContentDigestA=Low(snapshot.ContentDigest);value.ContentDigestB=High(snapshot.ContentDigest);return value;}
        public static PlanetSurfaceDescriptor FromView(in NativeSurfaceView view)
        {var value=FromRevision(view.Revision);value.ContentDigestA=Low(view.ContentDigest);value.ContentDigestB=High(view.ContentDigest);return value;}
        public bool Equals(PlanetSurfaceDescriptor other) => Epoch==other.Epoch &&
            math.all(RecipeDigestA==other.RecipeDigestA) && math.all(RecipeDigestB==other.RecipeDigestB) &&
            math.all(BaseDigestA==other.BaseDigestA) && math.all(BaseDigestB==other.BaseDigestB) && math.all(ContentDigestA==other.ContentDigestA) && math.all(ContentDigestB==other.ContentDigestB);
        public override bool Equals(object other) => other is PlanetSurfaceDescriptor descriptor && Equals(descriptor);
        public override int GetHashCode() => unchecked((int)(math.hash(RecipeDigestA)^math.hash(RecipeDigestB)^math.hash(BaseDigestA)^math.hash(BaseDigestB)^math.hash(ContentDigestA)^math.hash(ContentDigestB)^(uint)Epoch^(uint)(Epoch>>32)));
    }
}
