using UnityEditor;

namespace UnityEngine.Rendering.HighDefinition
{
    public static class PlanetSitePlacement
    {
        public static bool TryApply(PlanetSiteAsset site,PlanetSurfaceAddress picked,out string error)
        {
            error=null;
            if(!site || !site.Generator || !picked.IsValid){error="Choose a site and a valid surface point.";return false;}
            var address=site.Address;address.Latitude=picked.Latitude;address.Longitude=picked.Longitude;
            try {PlanetSiteLayout.Build(site.Generator.Definition,address,site.Settings);}
            catch(System.ArgumentException e){error=e.Message;return false;}
            Undo.RecordObject(site,"Place planet site");site.Address=address;EditorUtility.SetDirty(site);return true;
        }
    }
}
