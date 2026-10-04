using Unity.Collections;
using Unity.Mathematics;

namespace SpaceRunner.PlanetTerrain
{
    /// <summary>Canonical signed-height queries. No static registry, renderer or camera state enters a job.</summary>
    public static class SurfaceSampler
    {
        public static SurfaceSampleStatus TrySampleAttributes(in NativeSurfaceView view, double3 direction, out SurfaceAttributes attributes,
            SurfaceChannels required = SurfaceChannels.None)
        {
            return SampleAttributes(view, direction, SurfaceSamplingFootprint.Full, false, 0, default, 0, out attributes, required);
        }

        /// <summary>Derived render classification from the same filtered geometry. Full authority queries are unchanged.</summary>
        /// <remarks>The supplied normal is reused only when its effective metric sample step matches each captured profile.</remarks>
        public static SurfaceSampleStatus TrySampleRenderAttributes(in NativeSurfaceView view, double3 direction,
            SurfaceSamplingFootprint footprint, double geometryHeight, double3 geometryNormal, double geometryNormalSampleMetres,
            out SurfaceAttributes attributes, SurfaceChannels required = SurfaceChannels.None)
        {
            attributes = default;
            if (!footprint.IsValid || !math.isfinite(geometryHeight) || !math.all(math.isfinite(geometryNormal)) ||
                math.abs(math.lengthsq(geometryNormal) - 1) > 1e-8 || !math.isfinite(geometryNormalSampleMetres) || geometryNormalSampleMetres <= 0)
                return SurfaceSampleStatus.InvalidInput;
            if (footprint.Metres == 0) return TrySampleAttributes(view, direction, out attributes, required);
            return SampleAttributes(view, direction, footprint, true, geometryHeight, geometryNormal, geometryNormalSampleMetres, out attributes, required);
        }

        static SurfaceSampleStatus SampleAttributes(in NativeSurfaceView view, double3 direction, SurfaceSamplingFootprint footprint,
            bool geometryReady, double geometryHeight, double3 geometryNormal, double geometryNormalSampleMetres,
            out SurfaceAttributes attributes, SurfaceChannels required)
        {
            attributes = default;
            if (!view.Recipe.IsValid || !view.Revision.IsValid || !CubeSurface.TryLocate(direction, view.CanonicalTileLevel, out var key, out var uv))
                return SurfaceSampleStatus.InvalidInput;
            if (!view.Tiles.IsCreated) return SurfaceSampleStatus.NotReady;
            int found = FindTile(view, key); if (found < 0) return SurfaceSampleStatus.NotReady;
            var tile = view.Tiles[found]; var channels = tile.Channels;
            float4 weights = default, erosion = default;
            if ((channels & SurfaceChannels.MaterialWeights) != 0 && !TryBilinear(view.MaterialWeights, tile.AttributeOffset, new int2(tile.Resolution), uv, out weights))
                return SurfaceSampleStatus.IncompatibleData;
            if ((channels & SurfaceChannels.ErosionData) != 0 && !TryBilinear(view.ErosionData, tile.AttributeOffset, new int2(tile.Resolution), uv, out erosion))
                return SurfaceSampleStatus.IncompatibleData;
            CubeSurface.TryNormalize(direction, out var unit);
            bool structuralMaterials = view.StructuralField.Enabled && view.AutomaticMaterialProfile.IsValid && required != SurfaceChannels.ErosionData;
            for (int i = 0; i < view.Regions.Length; i++)
            {
                var region = view.Regions[i];
                if (region.Channels == SurfaceChannels.None || !RegionWeight(view, region, unit, out var regionUv, out var weight)) continue;
                if (!structuralMaterials && (region.Channels & SurfaceChannels.MaterialWeights) != 0)
                {
                    if (!TryBilinear(view.RegionMaterialWeights, region.AttributeOffset, region.Resolution, regionUv, out var imported)) return SurfaceSampleStatus.IncompatibleData;
                    if ((channels & SurfaceChannels.MaterialWeights) != 0) weights = math.lerp(weights, imported, (float)weight);
                    else if (weight >= 1) { weights = imported; channels |= SurfaceChannels.MaterialWeights; }
                }
                if ((region.Channels & SurfaceChannels.ErosionData) != 0)
                {
                    if (!TryBilinear(view.RegionErosionData, region.AttributeOffset, region.Resolution, regionUv, out var imported)) return SurfaceSampleStatus.IncompatibleData;
                    if ((channels & SurfaceChannels.ErosionData) != 0) erosion = math.lerp(erosion, imported, (float)weight);
                    else if (weight >= 1) { erosion = imported; channels |= SurfaceChannels.ErosionData; }
                }
                // A partial imported mask cannot invent the missing base material.
            }
            if (structuralMaterials)
            {
                if (!view.TileMaterialProvenance.IsCreated || view.TileMaterialProvenance.Length != view.Tiles.Length ||
                    !view.RegionalMaterialProfiles.IsCreated || view.RegionalMaterialProfiles.Length != view.Regions.Length) return SurfaceSampleStatus.IncompatibleData;
                var profile = view.AutomaticMaterialProfile;
                double height = geometryHeight; double3 normal = geometryNormal;
                var status = SurfaceSampleStatus.Ready;
                if (!geometryReady) status = TrySampleHeight(view, unit, footprint, out height);
                if (status != SurfaceSampleStatus.Ready) return status;
                if (!geometryReady || math.max(profile.NormalSampleMetres, footprint.Metres * .5) !=
                    math.max(geometryNormalSampleMetres, footprint.Metres * .5))
                    status = TrySampleNormal(view, unit, profile.NormalSampleMetres, footprint, out normal);
                if (status != SurfaceSampleStatus.Ready) return status;
                double cosine = math.clamp(math.dot(normal, unit), 1e-12, 1), slope = math.sqrt(math.max(0, 1 - cosine * cosine)) / cosine;
                double wetness = (channels & SurfaceChannels.ErosionData) != 0 ? erosion.y : 0;
                float4 final = profile.Evaluate(view.Recipe, unit, height, slope, wetness);
                for (int i = 0; i < view.Regions.Length; i++)
                {
                    var regionalProfile = view.RegionalMaterialProfiles[i];
                    if (!regionalProfile.IsValid || !RegionWeight(view, view.Regions[i], unit, out _, out double weight) || weight <= 0) continue;
                    double localSlope = slope;
                    if (regionalProfile.NormalSampleMetres != profile.NormalSampleMetres)
                    {
                        status = TrySampleNormal(view, unit, regionalProfile.NormalSampleMetres, footprint, out normal);
                        if (status != SurfaceSampleStatus.Ready) return status;
                        cosine = math.clamp(math.dot(normal, unit), 1e-12, 1); localSlope = math.sqrt(math.max(0, 1 - cosine * cosine)) / cosine;
                    }
                    final = math.lerp(final, regionalProfile.Evaluate(view.Recipe, unit, height, localSlope, wetness), (float)weight);
                }
                if ((channels & SurfaceChannels.MaterialWeights) != 0 && view.TileMaterialProvenance[found] == (int)SurfaceMaterialProvenance.Authored)
                    final = weights;
                for (int i = 0; i < view.Regions.Length; i++)
                {
                    var region = view.Regions[i];
                    if ((region.Channels & SurfaceChannels.MaterialWeights) == 0 || !RegionWeight(view, region, unit, out var localUv, out double weight) || weight <= 0) continue;
                    if (!TryBilinear(view.RegionMaterialWeights, region.AttributeOffset, region.Resolution, localUv, out var authored)) return SurfaceSampleStatus.IncompatibleData;
                    final = math.lerp(final, authored, (float)weight);
                }
                weights = final; channels |= SurfaceChannels.MaterialWeights;
            }
            if (channels == SurfaceChannels.None || (channels & required) != required) return SurfaceSampleStatus.NotReady;
            if ((channels & SurfaceChannels.MaterialWeights) != 0) weights /= math.csum(weights);
            attributes = new SurfaceAttributes(channels, weights, erosion); return SurfaceSampleStatus.Ready;
        }

        static bool RegionWeight(in NativeSurfaceView view, SurfaceRegionHeader region, double3 unit, out double2 uv, out double weight)
        {
            uv = default; weight = 0;
            if (!region.Projection.TryProject(unit, out var metres) || math.any(metres < region.Projection.MinimumMetres) || math.any(metres > region.Projection.MaximumMetres)) return false;
            uv = (metres - region.Projection.MinimumMetres) / (region.Projection.MaximumMetres - region.Projection.MinimumMetres);
            if (!TryBilinear(view.RegionMasks, region.MaskOffset, region.Resolution, uv, out var mask)) return false;
            weight = math.clamp(mask, 0, 1);
            if (region.BlendMetres > 0)
            {
                double t = math.clamp(math.cmin(math.min(metres - region.Projection.MinimumMetres, region.Projection.MaximumMetres - metres)) / region.BlendMetres, 0, 1);
                weight *= t * t * (3 - 2 * t);
            }
            return true;
        }
        static int FindTile(in NativeSurfaceView view, SurfaceTileKey key)
        {
            int low = 0, high = view.Tiles.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2, comparison = view.Tiles[middle].Key.CompareTo(key);
                if (comparison == 0) return middle;
                if (comparison < 0) low = middle + 1; else high = middle - 1;
            }
            return -1;
        }
        static bool TryBilinear(NativeArray<float4>.ReadOnly samples, int offset, int2 resolution, double2 uv, out float4 value)
        {
            value = default;
            if (!samples.IsCreated || math.any(resolution < 1) || offset < 0 || (long)offset + ((long)resolution.x + 1) * (resolution.y + 1) > samples.Length) return false;
            double2 grid = math.clamp(uv, 0, 1) * (double2)resolution;
            int x = (int)math.min(resolution.x - 1, math.floor(grid.x)), y = (int)math.min(resolution.y - 1, math.floor(grid.y));
            float2 f = (float2)(grid - new double2(x, y)); int row = resolution.x + 1, index = offset + y * row + x;
            value = math.lerp(math.lerp(samples[index], samples[index + 1], f.x), math.lerp(samples[index + row], samples[index + row + 1], f.x), f.y);
            return math.all(math.isfinite(value));
        }
        public static SurfaceSampleStatus TrySampleHeight(in NativeSurfaceView view, double3 direction, out double height)
        {
            var status = SampleHeight(view, direction, out var candidate);
            height = status == SurfaceSampleStatus.Ready ? candidate : 0;
            return status;
        }

        public static SurfaceSampleStatus TrySampleHeight(in NativeSurfaceView view, double3 direction,
            SurfaceSamplingFootprint footprint, out double height)
        {
            height = 0;
            if (!footprint.IsValid) return SurfaceSampleStatus.InvalidInput;
            if (footprint.Metres == 0) return TrySampleHeight(view, direction, out height);
            if (!view.Detail.IsValid) return SurfaceSampleStatus.InvalidInput;
            var status = SampleHeight(view, direction, out var candidate, footprint.DetailWeight(view.Detail.WavelengthMetres), footprint: footprint);
            if (status == SurfaceSampleStatus.Ready) height = candidate;
            return status;
        }

        /// <summary>Render-only regional low-pass. A missing/mismatched derived view is explicit NotReady.</summary>
        public static SurfaceSampleStatus TrySampleHeight(in NativeSurfaceView view, double3 direction,
            SurfaceSamplingFootprint footprint, in NativeSurfaceRegionFilterView filter, out double height)
        {
            height = 0;
            if (!footprint.IsValid) return SurfaceSampleStatus.InvalidInput;
            if (footprint.Metres == 0) return TrySampleHeight(view, direction, out height);
            if (!filter.Matches(view)) return SurfaceSampleStatus.NotReady;
            if (!view.Detail.IsValid) return SurfaceSampleStatus.InvalidInput;
            var status = SampleHeight(view, direction, out var value, footprint.DetailWeight(view.Detail.WavelengthMetres), filter, footprint);
            if (status == SurfaceSampleStatus.Ready) height = value;
            return status;
        }

        static SurfaceSampleStatus SampleHeight(in NativeSurfaceView view, double3 direction, out double height, double proceduralWeight = 1,
            NativeSurfaceRegionFilterView filter = default, SurfaceSamplingFootprint footprint = default)
        {
            height = 0;
            if (!view.Recipe.IsValid || !view.Revision.IsValid || !CubeSurface.TryLocate(direction, view.CanonicalTileLevel, out var key, out var uv))
                return SurfaceSampleStatus.InvalidInput;
            if (!view.Tiles.IsCreated || !view.Heights.IsCreated) return SurfaceSampleStatus.NotReady;
            int low = 0, high = view.Tiles.Length - 1, found = -1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2; int comparison = view.Tiles[middle].Key.CompareTo(key);
                if (comparison == 0) { found = middle; break; }
                if (comparison < 0) low = middle + 1; else high = middle - 1;
            }
            if (found < 0) return SurfaceSampleStatus.NotReady;
            var tile = view.Tiles[found];
            if (tile.Resolution != view.Resolution || !TryBilinear(view.Heights, tile.HeightOffset, new int2(tile.Resolution), uv, out height))
                return SurfaceSampleStatus.IncompatibleData;
            CubeSurface.TryNormalize(direction, out var unit);
            if (SurfaceRecipe.HasStructuralAuthority(view.Recipe.AlgorithmVersion))
            {
                if (view.StructuralField.SourceBaseDigest != view.Revision.BaseDigest || view.StructuralField.RawMacroResolution != view.Resolution)
                    return SurfaceSampleStatus.IncompatibleData;
                var structuralStatus = SurfaceStructuralMath.TrySampleBand(view.StructuralField, unit, footprint, out var band, out _);
                if (structuralStatus != SurfaceSampleStatus.Ready) return structuralStatus;
                height += band;
                if(view.StructuralField.MorphologyVersion==2)
                {
                    structuralStatus=SurfaceDrainageMath.TrySampleIncision(view.StructuralField.DrainageField,unit,height,footprint,out double incision,out _);
                    if(structuralStatus!=SurfaceSampleStatus.Ready)return structuralStatus;
                    height+=incision;
                }
            }
            var orogenStatus = SurfaceOrogenDetailMath.TrySample(view.OrogenDetail, unit, height, footprint, out double intrinsicDetail);
            if (orogenStatus != SurfaceSampleStatus.Ready) return orogenStatus;
            height += intrinsicDetail;
            double detailWeight = 1;
            for (int i = 0; i < view.Regions.Length; i++)
            {
                var region = view.Regions[i];
                if (footprint.Metres > 0 && filter.Matches(view))
                {
                    if (!region.Projection.TryProject(unit, out var projected)) continue;
                    if (!filter.TrySample(view, i, projected, footprint, out var pair)) return SurfaceSampleStatus.IncompatibleData;
                    if (region.Mode == SurfaceRegionMode.Replace)
                    { height = height * (1 - pair.y) + pair.x; intrinsicDetail *= 1 - pair.y; }
                    else
                    {
                        if (region.BaseDigest != view.Revision.BaseDigest) return SurfaceSampleStatus.IncompatibleData;
                        height += pair.x;
                        if (region.DetailPolicy == SurfaceDetailPolicy.Suppress)
                        { height -= intrinsicDetail * pair.y; intrinsicDetail *= 1 - pair.y; }
                    }
                    if (region.DetailPolicy == SurfaceDetailPolicy.Suppress) detailWeight *= 1 - pair.y;
                    continue;
                }
                if (!region.Projection.TryProject(unit, out var metres) || math.any(metres < region.Projection.MinimumMetres) || math.any(metres > region.Projection.MaximumMetres)) continue;
                var regionUv = (metres - region.Projection.MinimumMetres) / (region.Projection.MaximumMetres - region.Projection.MinimumMetres);
                if (!TryBilinear(view.RegionHeights, region.HeightOffset, region.Resolution, regionUv, out var value) ||
                    !TryBilinear(view.RegionMasks, region.MaskOffset, region.Resolution, regionUv, out var mask)) return SurfaceSampleStatus.IncompatibleData;
                double weight = math.clamp(mask, 0, 1);
                if (region.BlendMetres > 0)
                {
                    var border = math.min(metres - region.Projection.MinimumMetres, region.Projection.MaximumMetres - metres);
                    double t = math.clamp(math.cmin(border) / region.BlendMetres, 0, 1);
                    weight *= t * t * (3 - 2 * t);
                }
                if (region.Mode == SurfaceRegionMode.Replace)
                { height = math.lerp(height, value, weight); intrinsicDetail *= 1 - weight; }
                else
                {
                    if (region.BaseDigest != view.Revision.BaseDigest) return SurfaceSampleStatus.IncompatibleData;
                    height += value * weight;
                    if (region.DetailPolicy == SurfaceDetailPolicy.Suppress)
                    { height -= intrinsicDetail * weight; intrinsicDetail *= 1 - weight; }
                }
                if (region.DetailPolicy == SurfaceDetailPolicy.Suppress) detailWeight *= 1 - weight;
            }
            if (detailWeight > 0 && proceduralWeight > 0)
            {
                var detailStatus = view.Detail.TryHeight(unit, view.Recipe.Radius, out var detail);
                if (detailStatus != SurfaceSampleStatus.Ready) return detailStatus;
                height += detail * (proceduralWeight == 1 ? detailWeight : detailWeight * proceduralWeight);
            }
            for (int i = 0; i < view.Stamps.Length; i++)
            {
                var status = view.Stamps[i].TryHeight(unit, view.Recipe.Radius, out var displacement);
                if (status != SurfaceSampleStatus.Ready) return status;
                height += displacement;
            }
            return math.isfinite(height) && height > -view.Recipe.Radius ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.IncompatibleData;
        }

        public static SurfaceSampleStatus TrySamplePosition(in NativeSurfaceView view, double3 direction, out double3 position)
        {
            position = default;
            if (!CubeSurface.TryNormalize(direction, out var unit)) return SurfaceSampleStatus.InvalidInput;
            var status = TrySampleHeight(view, unit, out var height);
            if (status == SurfaceSampleStatus.Ready) position = unit * (view.Recipe.Radius + height);
            return status;
        }

        public static SurfaceSampleStatus TrySamplePosition(in NativeSurfaceView view, double3 direction,
            SurfaceSamplingFootprint footprint, out double3 position)
        {
            position = default;
            if (!footprint.IsValid) return SurfaceSampleStatus.InvalidInput;
            if (footprint.Metres == 0) return TrySamplePosition(view, direction, out position);
            if (!CubeSurface.TryNormalize(direction, out var unit)) return SurfaceSampleStatus.InvalidInput;
            var status = TrySampleHeight(view, unit, footprint, out var height);
            if (status == SurfaceSampleStatus.Ready) position = unit * (view.Recipe.Radius + height);
            return status;
        }

        public static SurfaceSampleStatus TrySamplePosition(in NativeSurfaceView view, double3 direction,
            SurfaceSamplingFootprint footprint, in NativeSurfaceRegionFilterView filter, out double3 position)
        {
            position = default;
            if (!CubeSurface.TryNormalize(direction, out var unit)) return SurfaceSampleStatus.InvalidInput;
            var status = TrySampleHeight(view, unit, footprint, filter, out var height);
            if (status == SurfaceSampleStatus.Ready) position = unit * (view.Recipe.Radius + height);
            return status;
        }

        public static SurfaceSampleStatus TrySampleNormal(in NativeSurfaceView view, double3 direction, double sampleMetres, out double3 normal)
        {
            normal = default;
            if (!view.Recipe.IsValid || !CubeSurface.TryNormalize(direction, out var unit) || !math.isfinite(sampleMetres) ||
                sampleMetres <= 0 || sampleMetres > view.Recipe.Radius * .25) return SurfaceSampleStatus.InvalidInput;
            var tangent = math.normalize(math.cross(math.abs(unit.y) < .9 ? new double3(0, 1, 0) : new double3(1, 0, 0), unit));
            var bitangent = math.cross(unit, tangent);
            double angle = sampleMetres / view.Recipe.Radius, sin = math.sin(angle), cos = math.cos(angle);
            var a = unit * cos + tangent * sin; var b = unit * cos - tangent * sin;
            var c = unit * cos + bitangent * sin; var d = unit * cos - bitangent * sin;
            var status = TrySampleHeight(view, a, out var ha); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, b, out var hb); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, c, out var hc); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, d, out var hd); if (status != SurfaceSampleStatus.Ready) return status;
            // Relative form avoids subtracting two nearly equal positions of planetary magnitude.
            var along = (a - b) * view.Recipe.Radius + a * ha - b * hb;
            var across = (c - d) * view.Recipe.Radius + c * hc - d * hd;
            return CubeSurface.TryNormalize(math.cross(along, across), out normal) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.IncompatibleData;
        }

        public static SurfaceSampleStatus TrySampleNormal(in NativeSurfaceView view, double3 direction, double sampleMetres,
            SurfaceSamplingFootprint footprint, out double3 normal)
        {
            normal = default;
            if (!footprint.IsValid) return SurfaceSampleStatus.InvalidInput;
            if (footprint.Metres == 0) return TrySampleNormal(view, direction, sampleMetres, out normal);
            if (!view.Recipe.IsValid || !CubeSurface.TryNormalize(direction, out var unit) || !math.isfinite(sampleMetres) || sampleMetres <= 0)
                return SurfaceSampleStatus.InvalidInput;
            double step = math.max(sampleMetres, footprint.Metres * .5);
            if (step > view.Recipe.Radius * .25) return SurfaceSampleStatus.InvalidInput;
            var tangent = math.normalize(math.cross(math.abs(unit.y) < .9 ? new double3(0, 1, 0) : new double3(1, 0, 0), unit));
            var bitangent = math.cross(unit, tangent);
            double angle = step / view.Recipe.Radius, sin = math.sin(angle), cos = math.cos(angle);
            var a = unit * cos + tangent * sin; var b = unit * cos - tangent * sin;
            var c = unit * cos + bitangent * sin; var d = unit * cos - bitangent * sin;
            var status = TrySampleHeight(view, a, footprint, out var ha); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, b, footprint, out var hb); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, c, footprint, out var hc); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, d, footprint, out var hd); if (status != SurfaceSampleStatus.Ready) return status;
            var along = (a - b) * view.Recipe.Radius + a * ha - b * hb;
            var across = (c - d) * view.Recipe.Radius + c * hc - d * hd;
            return CubeSurface.TryNormalize(math.cross(along, across), out normal) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.IncompatibleData;
        }

        public static SurfaceSampleStatus TrySampleNormal(in NativeSurfaceView view, double3 direction, double sampleMetres,
            SurfaceSamplingFootprint footprint, in NativeSurfaceRegionFilterView filter, out double3 normal)
        {
            normal = default;
            if (!footprint.IsValid) return SurfaceSampleStatus.InvalidInput;
            if (footprint.Metres == 0) return TrySampleNormal(view, direction, sampleMetres, out normal);
            if (!filter.Matches(view)) return SurfaceSampleStatus.NotReady;
            if (!view.Recipe.IsValid || !CubeSurface.TryNormalize(direction, out var unit) || !math.isfinite(sampleMetres) || sampleMetres <= 0)
                return SurfaceSampleStatus.InvalidInput;
            double step = math.max(sampleMetres, footprint.Metres * .5);
            if (step > view.Recipe.Radius * .25) return SurfaceSampleStatus.InvalidInput;
            var tangent = math.normalize(math.cross(math.abs(unit.y) < .9 ? new double3(0, 1, 0) : new double3(1, 0, 0), unit));
            var bitangent = math.cross(unit, tangent);
            double angle = step / view.Recipe.Radius, sin = math.sin(angle), cos = math.cos(angle);
            var a = unit * cos + tangent * sin; var b = unit * cos - tangent * sin;
            var c = unit * cos + bitangent * sin; var d = unit * cos - bitangent * sin;
            var status = TrySampleHeight(view, a, footprint, filter, out var ha); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, b, footprint, filter, out var hb); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, c, footprint, filter, out var hc); if (status != SurfaceSampleStatus.Ready) return status;
            status = TrySampleHeight(view, d, footprint, filter, out var hd); if (status != SurfaceSampleStatus.Ready) return status;
            var along = (a - b) * view.Recipe.Radius + a * ha - b * hb;
            var across = (c - d) * view.Recipe.Radius + c * hc - d * hd;
            return CubeSurface.TryNormalize(math.cross(along, across), out normal) ? SurfaceSampleStatus.Ready : SurfaceSampleStatus.IncompatibleData;
        }

        static bool TryBilinear(NativeArray<float>.ReadOnly samples, int offset, int2 resolution, double2 uv, out double value)
        {
            value = 0;
            if (!samples.IsCreated || math.any(resolution < 1) || !math.all(math.isfinite(uv)) || math.any(uv < 0) || math.any(uv > 1) ||
                offset < 0 || (long)offset + ((long)resolution.x + 1) * (resolution.y + 1) > samples.Length) return false;
            var grid = math.clamp(uv, 0, 1) * (double2)resolution;
            int x = (int)math.min(resolution.x - 1, math.floor(grid.x)), y = (int)math.min(resolution.y - 1, math.floor(grid.y));
            var fraction = grid - new double2(x, y); int row = resolution.x + 1, index = offset + y * row + x;
            value = math.lerp(math.lerp((double)samples[index], samples[index + 1], fraction.x),
                math.lerp((double)samples[index + row], samples[index + row + 1], fraction.x), fraction.y);
            return math.isfinite(value);
        }
    }
}
