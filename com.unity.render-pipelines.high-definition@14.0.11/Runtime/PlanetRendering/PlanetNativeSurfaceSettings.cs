using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering.HighDefinition
{
    [Serializable]
    public struct PlanetNativeSurfaceSettings
    {
        public double PatchSize,ReceiverHalfSize,ShadowHalfSize,RecenterDistance;
        public int Resolution,PatchesPerFrame,MaximumResidentPatches;
        public static PlanetNativeSurfaceSettings Default=>new PlanetNativeSurfaceSettings
        {PatchSize=128,ReceiverHalfSize=256,ShadowHalfSize=512,RecenterDistance=64,Resolution=32,PatchesPerFrame=4,MaximumResidentPatches=128};
        public bool IsValid=>math.isfinite(PatchSize)&&PatchSize>=1&&PatchSize<=1024&&math.isfinite(ReceiverHalfSize)&&ReceiverHalfSize>=PatchSize&&
            math.isfinite(ShadowHalfSize)&&ShadowHalfSize>=ReceiverHalfSize&&ShadowHalfSize<=4096&&math.isfinite(RecenterDistance)&&
            RecenterDistance>0&&RecenterDistance<=ReceiverHalfSize*.5&&Resolution>=2&&Resolution<=128&&(Resolution&(Resolution-1))==0&&
            PatchesPerFrame>=1&&PatchesPerFrame<=128&&MaximumResidentPatches>=4&&MaximumResidentPatches<=4096&&
            // Active and replacement banks must fit together. Abandoned workers retain
            // their reservation until completion; preparation waits rather than exceeding it.
            math.pow(math.ceil(ShadowHalfSize/PatchSize)*2,2)*2<=MaximumResidentPatches;
    }
}
