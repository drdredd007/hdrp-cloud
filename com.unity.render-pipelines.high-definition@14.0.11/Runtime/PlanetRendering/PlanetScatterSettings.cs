using System;
using SpaceRunner.PlanetTerrain;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Render residency is bounded independently of the host's physical collider interest.</summary>
    [CreateAssetMenu(menuName="Rendering/Planet Scatter Settings")]
    public sealed class PlanetScatterSettings : ScriptableObject
    {
        public PlanetScatterSpecies[] Species=Array.Empty<PlanetScatterSpecies>();
        [Min(1)] public int MaximumResidentCells=128;
        [Min(1)] public int MaximumResidentCandidates=65536;
        public long MaximumResidentBytes=128L*1024*1024;
        [Min(1)] public int MaximumNewCellsPerFrame=4;
        [Min(0)] public float CellRetentionSeconds=2;
        public bool Enabled=true;
        public bool IsValid
        {
            get
            {
                if(Species==null||Species.Length==0||Species.Length>SurfaceScatterProfile.MaximumSpecies||
                    MaximumResidentCells<1||MaximumResidentCandidates<1||MaximumResidentBytes<1||MaximumNewCellsPerFrame<1||
                    float.IsNaN(CellRetentionSeconds)||float.IsInfinity(CellRetentionSeconds)||CellRetentionSeconds<0)return false;
                for(int i=0;i<Species.Length;i++)
                {
                    if(!Species[i]||!Species[i].IsValid)return false;
                    for(int j=0;j<i;j++)if(Species[j].SpeciesId==Species[i].SpeciesId)return false;
                }
                return true;
            }
        }
        public SurfaceScatterProfile CapturePlacement()
        {
            if(!IsValid)throw new InvalidOperationException("Scatter settings require valid unique species and bounded residency.");
            var captured=new SurfaceScatterSpecies[Species.Length];
            for(int i=0;i<captured.Length;i++)captured[i]=Species[i].Placement;
            return new SurfaceScatterProfile(captured);
        }
    }
}
