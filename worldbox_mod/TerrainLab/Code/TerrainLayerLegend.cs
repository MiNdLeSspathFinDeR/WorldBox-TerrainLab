using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using NeoModLoader.General;
using UnityEngine;
using UnityEngine.UI;

namespace TerrainLab
{
    public enum TerrainLayerLegendKind
    {
        Continuous,
        Categories
    }

    public sealed class TerrainLayerLegendEntry
    {
        public TerrainLayerLegendEntry(
            string id,
            string labelKey,
            Color32 color,
            Sprite sprite = null,
            bool lineSymbol = false)
        {
            Id = id ?? string.Empty;
            LabelKey = labelKey ?? string.Empty;
            Color = color;
            Sprite = sprite;
            LineSymbol = lineSymbol;
        }

        public string Id { get; }

        public string LabelKey { get; }

        public Color32 Color { get; }

        public Sprite Sprite { get; }

        public bool LineSymbol { get; }
    }

    public sealed class TerrainLayerLegendDescriptor
    {
        private readonly Func<float, Color32> _colorSampler;
        private readonly IReadOnlyList<TerrainLayerLegendEntry> _entries;

        private TerrainLayerLegendDescriptor(
            string id,
            string titleKey,
            string unitKey,
            double minimum,
            double maximum,
            Func<float, Color32> colorSampler,
            IReadOnlyList<TerrainLayerLegendEntry> entries)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            TitleKey = titleKey ?? throw new ArgumentNullException(nameof(titleKey));
            UnitKey = unitKey ?? string.Empty;
            Minimum = minimum;
            Maximum = maximum;
            _colorSampler = colorSampler;
            _entries = entries ?? Array.Empty<TerrainLayerLegendEntry>();
            Kind = colorSampler == null
                ? TerrainLayerLegendKind.Categories
                : TerrainLayerLegendKind.Continuous;
        }

        public string Id { get; }

        public string TitleKey { get; }

        public string UnitKey { get; }

        public double Minimum { get; }

        public double Maximum { get; }

        public TerrainLayerLegendKind Kind { get; }

        public IReadOnlyList<TerrainLayerLegendEntry> Entries => _entries;

        public bool IncludesZero =>
            Kind == TerrainLayerLegendKind.Continuous &&
            Minimum < 0d && Maximum > 0d;

        public string Signature
        {
            get
            {
                string signature = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2:R}|{3:R}|{4}",
                    Id,
                    Kind,
                    Minimum,
                    Maximum,
                    _entries.Count);
                for (int index = 0; index < _entries.Count; index++)
                {
                    TerrainLayerLegendEntry entry = _entries[index];
                    signature += "|" + entry.Id + ":" +
                                 (entry.Sprite == null
                                     ? 0
                                     : entry.Sprite.GetInstanceID());
                }

                return signature;
            }
        }

        public Color32 Sample(float normalized)
        {
            if (_colorSampler == null)
            {
                return new Color32(0, 0, 0, 0);
            }

            return _colorSampler(Mathf.Clamp01(normalized));
        }

        public static TerrainLayerLegendDescriptor Continuous(
            string id,
            string titleKey,
            string unitKey,
            double minimum,
            double maximum,
            Func<float, Color32> colorSampler)
        {
            if (colorSampler == null)
            {
                throw new ArgumentNullException(nameof(colorSampler));
            }

            if (double.IsNaN(minimum) || double.IsInfinity(minimum) ||
                double.IsNaN(maximum) || double.IsInfinity(maximum) ||
                maximum < minimum)
            {
                throw new ArgumentOutOfRangeException(nameof(maximum));
            }

            if (Math.Abs(maximum - minimum) < double.Epsilon)
            {
                maximum = minimum + 1d;
            }

            return new TerrainLayerLegendDescriptor(
                id,
                titleKey,
                unitKey,
                minimum,
                maximum,
                colorSampler,
                null);
        }

        public static TerrainLayerLegendDescriptor Categories(
            string id,
            string titleKey,
            IReadOnlyList<TerrainLayerLegendEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                throw new ArgumentException(
                    "A categorical legend needs at least one entry.",
                    nameof(entries));
            }

            return new TerrainLayerLegendDescriptor(
                id,
                titleKey,
                string.Empty,
                0d,
                0d,
                null,
                entries);
        }
    }

    public static class TerrainLayerLegendCatalog
    {
        public static TerrainLayerLegendDescriptor CreateElevation(
            short minimum,
            short maximum,
            short seaLevel)
        {
            return TerrainLayerLegendDescriptor.Continuous(
                "dem_elevation",
                "terrain_lab_elevation_overlay",
                "terrain_lab_legend_unit_metres",
                minimum,
                maximum,
                normalized =>
                {
                    short elevation = InterpolateShort(minimum, maximum, normalized);
                    return LegendColor(TerrainElevationOverlay.GetColor(
                        elevation,
                        seaLevel,
                        minimum,
                        maximum));
                });
        }

        public static TerrainLayerLegendDescriptor CreateData(
            TerrainDataOverlayMode mode,
            TerrainWorldState state)
        {
            if (state == null)
            {
                return null;
            }

            switch (mode)
            {
                case TerrainDataOverlayMode.Landform:
                    return TerrainLayerLegendDescriptor.Categories(
                        "data_landform",
                        "terrain_lab_data_overlay_landform",
                        CreateLandformEntries(state));
                case TerrainDataOverlayMode.Material:
                    return TerrainLayerLegendDescriptor.Categories(
                        "data_material",
                        "terrain_lab_data_overlay_material",
                        CreateMaterialEntries(state));
                case TerrainDataOverlayMode.Contours:
                    return TerrainLayerLegendDescriptor.Categories(
                        "data_contours",
                        "terrain_lab_data_overlay_contours",
                        new[]
                        {
                            new TerrainLayerLegendEntry(
                                "contour_minor",
                                "terrain_lab_legend_contour_minor",
                                new Color32(235, 235, 225, 255),
                                null,
                                true),
                            new TerrainLayerLegendEntry(
                                "contour_major",
                                "terrain_lab_legend_contour_major",
                                new Color32(255, 204, 70, 255),
                                null,
                                true),
                            new TerrainLayerLegendEntry(
                                "contour_sea",
                                "terrain_lab_legend_contour_sea",
                                new Color32(55, 220, 245, 255),
                                null,
                                true)
                        });
                case TerrainDataOverlayMode.ManagedWater:
                    return TerrainLayerLegendDescriptor.Categories(
                        "data_managed_water",
                        "terrain_lab_data_overlay_managed_water",
                        new[]
                        {
                            CreateWorldCategoryEntry(
                                state,
                                state.WaterDynamics?.ManagedMask,
                                1,
                                "managed_water",
                                "terrain_lab_legend_managed_water",
                                TerrainDataOverlay.GetManagedWaterColor(1))
                        });
                case TerrainDataOverlayMode.WaterStorage:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "data_water_storage",
                        "terrain_lab_data_overlay_water_storage",
                        "terrain_lab_legend_unit_metres",
                        0d,
                        byte.MaxValue,
                        normalized => LegendColor(
                            TerrainDataOverlay.GetWaterStorageColor(
                                (byte)Mathf.RoundToInt(
                                    normalized * byte.MaxValue))));
                case TerrainDataOverlayMode.HydroFeature:
                    return TerrainLayerLegendDescriptor.Categories(
                        "data_hydro_feature",
                        "terrain_lab_data_overlay_hydro_feature",
                        new[]
                        {
                            CreateWorldCategoryEntry(
                                state,
                                state.WaterDynamics?.HydroFeature,
                                (byte)TerrainHydroFeature.River,
                                "hydro_river",
                                "terrain_lab_hydro_feature_river",
                                TerrainDataOverlay.GetHydroFeatureColor(
                                    (byte)TerrainHydroFeature.River)),
                            CreateWorldCategoryEntry(
                                state,
                                state.WaterDynamics?.HydroFeature,
                                (byte)TerrainHydroFeature.Waterbody,
                                "hydro_waterbody",
                                "terrain_lab_hydro_feature_waterbody",
                                TerrainDataOverlay.GetHydroFeatureColor(
                                    (byte)TerrainHydroFeature.Waterbody))
                        });
                case TerrainDataOverlayMode.Moisture:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "data_moisture",
                        "terrain_lab_data_overlay_moisture",
                        "terrain_lab_legend_unit_percent",
                        0d,
                        100d,
                        normalized => LegendColor(
                            TerrainDataOverlay.GetMoistureColor(
                                (byte)Mathf.RoundToInt(
                                    normalized * byte.MaxValue))));
                case TerrainDataOverlayMode.Erodibility:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "data_erodibility",
                        "terrain_lab_data_overlay_erodibility",
                        "terrain_lab_legend_unit_percent",
                        0d,
                        100d,
                        normalized => LegendColor(
                            TerrainDataOverlay.GetErodibilityColor(
                                (byte)Mathf.Clamp(
                                    Mathf.RoundToInt(
                                        normalized *
                                        TerrainRiverValleyModel.MaximumEncodedValue),
                                    1,
                                    TerrainRiverValleyModel.MaximumEncodedValue))));
                case TerrainDataOverlayMode.LocalSlope:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "data_local_slope",
                        "terrain_lab_data_overlay_local_slope",
                        "terrain_lab_legend_unit_degrees",
                        0d,
                        90d,
                        normalized => LegendColor(
                            TerrainDataOverlay.GetLocalSlopeColor(
                                (byte)Mathf.RoundToInt(
                                    normalized *
                                    TerrainRiverValleyModel.MaximumEncodedValue))));
                case TerrainDataOverlayMode.LocalAspect:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "data_local_aspect",
                        "terrain_lab_data_overlay_local_aspect",
                        "terrain_lab_legend_unit_degrees",
                        0d,
                        360d,
                        normalized => LegendColor(
                            TerrainDataOverlay.GetLocalAspectColor(
                                (byte)Mathf.Min(
                                    TerrainRiverValleyModel.MaximumEncodedValue,
                                    Mathf.RoundToInt(
                                        normalized *
                                        TerrainRiverValleyModel.MaximumEncodedValue)))));
                default:
                    return null;
            }
        }

        public static TerrainLayerLegendDescriptor CreateRelief(
            TerrainReliefOverlayMode mode,
            TerrainWorldState state,
            TerrainReliefResult result)
        {
            if (state == null || result?.Statistics == null)
            {
                return null;
            }

            TerrainReliefStatistics statistics = result.Statistics;
            switch (mode)
            {
                case TerrainReliefOverlayMode.Hypsometry:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "relief_hypsometry",
                        "terrain_lab_relief_overlay_hypsometry",
                        "terrain_lab_legend_unit_metres",
                        statistics.MinimumElevation,
                        statistics.MaximumElevation,
                        normalized => LegendColor(
                            TerrainReliefOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state.SeaLevel,
                                statistics)));
                case TerrainReliefOverlayMode.Slope:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "relief_slope",
                        "terrain_lab_relief_overlay_slope",
                        "terrain_lab_legend_unit_degrees",
                        0d,
                        statistics.MaximumSlopeTenths / 10d,
                        normalized => LegendColor(
                            TerrainReliefOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state.SeaLevel,
                                statistics)));
                case TerrainReliefOverlayMode.Aspect:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "relief_aspect",
                        "terrain_lab_relief_overlay_aspect",
                        "terrain_lab_legend_unit_degrees",
                        0d,
                        360d,
                        normalized => LegendColor(
                            TerrainReliefOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state.SeaLevel,
                                statistics)));
                case TerrainReliefOverlayMode.Hillshade:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "relief_hillshade",
                        "terrain_lab_relief_overlay_hillshade",
                        "terrain_lab_legend_unit_intensity",
                        0d,
                        byte.MaxValue,
                        normalized => LegendColor(
                            TerrainReliefOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state.SeaLevel,
                                statistics)));
                case TerrainReliefOverlayMode.Ruggedness:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "relief_ruggedness",
                        "terrain_lab_relief_overlay_ruggedness",
                        "terrain_lab_legend_unit_metres",
                        0d,
                        statistics.MaximumRuggedness,
                        normalized => LegendColor(
                            TerrainReliefOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state.SeaLevel,
                                statistics)));
                default:
                    return null;
            }
        }

        public static TerrainLayerLegendDescriptor CreateHydrology(
            TerrainHydrologyOverlayMode mode,
            TerrainWorldState state,
            TerrainHydrologyResult result)
        {
            if (state == null || result?.Statistics == null)
            {
                return null;
            }

            TerrainHydrologyStatistics statistics = result.Statistics;
            switch (mode)
            {
                case TerrainHydrologyOverlayMode.Streams:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "hydrology_streams",
                        "terrain_lab_hydrology_overlay_streams",
                        "terrain_lab_legend_unit_cells",
                        Math.Max(1, result.StreamThreshold),
                        Math.Max(
                            Math.Max(1, result.StreamThreshold),
                            statistics.MaximumAccumulation),
                        normalized => LegendColor(
                            TerrainHydrologyOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state,
                                result)));
                case TerrainHydrologyOverlayMode.Accumulation:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "hydrology_accumulation",
                        "terrain_lab_hydrology_overlay_accumulation",
                        "terrain_lab_legend_unit_cells",
                        0d,
                        Math.Max(1u, statistics.MaximumAccumulation),
                        normalized => LegendColor(
                            TerrainHydrologyOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state,
                                result)));
                case TerrainHydrologyOverlayMode.FillDepth:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "hydrology_fill_depth",
                        "terrain_lab_hydrology_overlay_fill",
                        "terrain_lab_legend_unit_metres",
                        0d,
                        Math.Max(1, statistics.MaximumFillDepth),
                        normalized => LegendColor(
                            TerrainHydrologyOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state,
                                result)));
                case TerrainHydrologyOverlayMode.FilledElevation:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "hydrology_filled_elevation",
                        "terrain_lab_hydrology_overlay_filled_elevation",
                        "terrain_lab_legend_unit_metres",
                        TerrainElevationEncoding.Minimum,
                        TerrainElevationEncoding.Maximum,
                        normalized => LegendColor(
                            TerrainHydrologyOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state,
                                result)));
                case TerrainHydrologyOverlayMode.FlowDirection:
                    return TerrainLayerLegendDescriptor.Categories(
                        "hydrology_flow_direction",
                        "terrain_lab_hydrology_overlay_flow_direction",
                        CreateDirectionEntries());
                case TerrainHydrologyOverlayMode.Watersheds:
                    return TerrainLayerLegendDescriptor.Categories(
                        "hydrology_watersheds",
                        "terrain_lab_hydrology_overlay_watersheds",
                        new[]
                        {
                            new TerrainLayerLegendEntry(
                                "watershed",
                                "terrain_lab_legend_watershed",
                                LegendColor(
                                    TerrainHydrologyOverlay.GetWatershedColor(1)))
                        });
                case TerrainHydrologyOverlayMode.StreamOrder:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "hydrology_stream_order",
                        "terrain_lab_hydrology_overlay_stream_order",
                        "terrain_lab_legend_unit_order",
                        1d,
                        Math.Max(1, (int)statistics.MaximumStreamOrder),
                        normalized => LegendColor(
                            TerrainHydrologyOverlay.GetLegendColor(
                                mode,
                                normalized,
                                state,
                                result)));
                default:
                    return null;
            }
        }

        public static TerrainLayerLegendDescriptor CreateErosion(
            TerrainErosionOverlayMode mode,
            TerrainWorldState state,
            TerrainErosionResult result)
        {
            if (state == null || result?.Statistics == null)
            {
                return null;
            }

            TerrainErosionStatistics statistics = result.Statistics;
            switch (mode)
            {
                case TerrainErosionOverlayMode.NetChange:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "erosion_net",
                        "terrain_lab_erosion_overlay_net",
                        "terrain_lab_legend_unit_metres",
                        -Math.Max(1, statistics.MaximumCut),
                        Math.Max(1, statistics.MaximumFill),
                        normalized => LegendColor(
                            TerrainErosionOverlay.GetNetChangeColor(
                                Mathf.RoundToInt(Mathf.Lerp(
                                    -Math.Max(1, statistics.MaximumCut),
                                    Math.Max(1, statistics.MaximumFill),
                                    normalized)),
                                statistics.MaximumCut,
                                statistics.MaximumFill)));
                case TerrainErosionOverlayMode.Erosion:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "erosion_cut",
                        "terrain_lab_erosion_overlay_cut",
                        "terrain_lab_legend_unit_metres",
                        -Math.Max(1, statistics.MaximumCut),
                        0d,
                        normalized => LegendColor(
                            TerrainErosionOverlay.GetErosionColor(
                                Mathf.RoundToInt(Mathf.Lerp(
                                    -Math.Max(1, statistics.MaximumCut),
                                    0f,
                                    normalized)),
                                statistics.MaximumCut)));
                case TerrainErosionOverlayMode.Deposition:
                    return TerrainLayerLegendDescriptor.Continuous(
                        "erosion_fill",
                        "terrain_lab_erosion_overlay_fill",
                        "terrain_lab_legend_unit_metres",
                        0d,
                        Math.Max(1, statistics.MaximumFill),
                        normalized => LegendColor(
                            TerrainErosionOverlay.GetDepositionColor(
                                Mathf.RoundToInt(
                                    Math.Max(1, statistics.MaximumFill) *
                                    normalized),
                                statistics.MaximumFill)));
                case TerrainErosionOverlayMode.ResultElevation:
                    FindElevationRange(
                        result.ResultElevation,
                        out short minimum,
                        out short maximum);
                    return TerrainLayerLegendDescriptor.Continuous(
                        "erosion_result",
                        "terrain_lab_erosion_overlay_result",
                        "terrain_lab_legend_unit_metres",
                        minimum,
                        maximum,
                        normalized => LegendColor(
                            TerrainErosionOverlay.GetResultElevationColor(
                                InterpolateShort(
                                    minimum,
                                    maximum,
                                    normalized),
                                minimum,
                                maximum)));
                default:
                    return null;
            }
        }

        private static IReadOnlyList<TerrainLayerLegendEntry>
            CreateLandformEntries(TerrainWorldState state)
        {
            HashSet<byte> present = GetPresentValues(state.Landform);
            Dictionary<byte, Sprite> sprites = ResolveWorldSprites(
                state,
                state.Landform);
            List<TerrainLayerLegendEntry> entries =
                new List<TerrainLayerLegendEntry>();
            for (TerrainLandform value = TerrainLandform.Unknown;
                 value <= TerrainLandform.Artificial;
                 value++)
            {
                byte encoded = (byte)value;
                if (!present.Contains(encoded))
                {
                    continue;
                }

                sprites.TryGetValue(encoded, out Sprite sprite);
                string token = value.ToString().ToLowerInvariant();
                entries.Add(new TerrainLayerLegendEntry(
                    "landform_" + token,
                    "terrain_lab_landform_" + token,
                    LegendColor(TerrainDataOverlay.GetLandformColor(encoded)),
                    sprite));
            }

            return entries;
        }

        private static IReadOnlyList<TerrainLayerLegendEntry>
            CreateMaterialEntries(TerrainWorldState state)
        {
            HashSet<byte> present = GetPresentValues(state.Material);
            Dictionary<byte, Sprite> sprites = ResolveWorldSprites(
                state,
                state.Material);
            List<TerrainLayerLegendEntry> entries =
                new List<TerrainLayerLegendEntry>();
            for (TerrainMaterial value = TerrainMaterial.Unknown;
                 value <= TerrainMaterial.Clay;
                 value++)
            {
                byte encoded = (byte)value;
                if (!present.Contains(encoded))
                {
                    continue;
                }

                sprites.TryGetValue(encoded, out Sprite sprite);
                string token = value.ToString().ToLowerInvariant();
                entries.Add(new TerrainLayerLegendEntry(
                    "material_" + token,
                    "terrain_lab_material_" + token,
                    LegendColor(TerrainDataOverlay.GetMaterialColor(encoded)),
                    sprite));
            }

            return entries;
        }

        private static HashSet<byte> GetPresentValues(byte[] values)
        {
            HashSet<byte> result = new HashSet<byte>();
            if (values == null)
            {
                return result;
            }

            for (int index = 0; index < values.Length; index++)
            {
                result.Add(values[index]);
            }

            return result;
        }

        private static IReadOnlyList<TerrainLayerLegendEntry>
            CreateDirectionEntries()
        {
            List<TerrainLayerLegendEntry> entries =
                new List<TerrainLayerLegendEntry>();
            for (TerrainFlowDirection direction = TerrainFlowDirection.East;
                 direction <= TerrainFlowDirection.SouthEast;
                 direction++)
            {
                string token = direction.ToString().ToLowerInvariant();
                entries.Add(new TerrainLayerLegendEntry(
                    "direction_" + token,
                    "terrain_lab_legend_direction_" + token,
                    LegendColor(
                        TerrainHydrologyOverlay.GetFlowDirectionColor(
                            (byte)direction))));
            }

            return entries;
        }

        private static TerrainLayerLegendEntry CreateWorldCategoryEntry(
            TerrainWorldState state,
            byte[] values,
            byte target,
            string id,
            string labelKey,
            Color32 color)
        {
            Sprite sprite = ResolveWorldSprite(state, values, target);
            return new TerrainLayerLegendEntry(
                id,
                labelKey,
                LegendColor(color),
                sprite);
        }

        private static Dictionary<byte, Sprite> ResolveWorldSprites(
            TerrainWorldState state,
            byte[] values)
        {
            Dictionary<byte, Sprite> result = new Dictionary<byte, Sprite>();
            WorldTile[] tiles = state?.GetCurrentWorldTilesForRuntime();
            if (tiles == null || values == null || values.Length != tiles.Length)
            {
                return result;
            }

            for (int index = 0; index < values.Length; index++)
            {
                byte value = values[index];
                if (result.ContainsKey(value))
                {
                    continue;
                }

                Sprite sprite = TerrainLayerSpriteResolver.TryGetTileSprite(
                    tiles[index]);
                if (sprite != null)
                {
                    result[value] = sprite;
                }
            }

            return result;
        }

        private static Sprite ResolveWorldSprite(
            TerrainWorldState state,
            byte[] values,
            byte target)
        {
            WorldTile[] tiles = state?.GetCurrentWorldTilesForRuntime();
            if (tiles == null || values == null || values.Length != tiles.Length)
            {
                return null;
            }

            for (int index = 0; index < values.Length; index++)
            {
                if (values[index] != target)
                {
                    continue;
                }

                Sprite sprite = TerrainLayerSpriteResolver.TryGetTileSprite(
                    tiles[index]);
                if (sprite != null)
                {
                    return sprite;
                }
            }

            return null;
        }

        private static short InterpolateShort(
            short minimum,
            short maximum,
            float normalized)
        {
            return (short)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(minimum, maximum, normalized)),
                short.MinValue,
                short.MaxValue);
        }

        private static void FindElevationRange(
            short[] elevation,
            out short minimum,
            out short maximum)
        {
            minimum = short.MaxValue;
            maximum = short.MinValue;
            if (elevation != null)
            {
                for (int index = 0; index < elevation.Length; index++)
                {
                    short value = elevation[index];
                    if (!TerrainElevationEncoding.IsDataValue(value))
                    {
                        continue;
                    }

                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }
            }

            if (minimum == short.MaxValue)
            {
                minimum = 0;
                maximum = 1;
            }
            else if (minimum == maximum)
            {
                maximum = (short)Math.Min(
                    TerrainElevationEncoding.Maximum,
                    minimum + 1);
                if (minimum == maximum)
                {
                    minimum--;
                }
            }
        }

        private static Color32 LegendColor(Color32 color)
        {
            if (color.a == 0)
            {
                return color;
            }

            color.a = byte.MaxValue;
            return color;
        }
    }

    internal static class TerrainLayerSpriteResolver
    {
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy;

        public static Sprite TryGetTileSprite(WorldTile tile)
        {
            object tileType = tile?.Type;
            if (tileType == null)
            {
                return null;
            }

            try
            {
                FieldInfo spritesField = tileType.GetType().GetField(
                    "sprites",
                    InstanceMembers);
                object sprites = spritesField?.GetValue(tileType);
                if (sprites == null)
                {
                    return null;
                }

                PropertyInfo mainProperty = sprites.GetType().GetProperty(
                    "main",
                    InstanceMembers);
                object mainTile = mainProperty?.GetValue(sprites, null);
                if (mainTile == null)
                {
                    return null;
                }

                PropertyInfo spriteProperty = mainTile.GetType().GetProperty(
                    "sprite",
                    InstanceMembers);
                return spriteProperty?.GetValue(mainTile, null) as Sprite;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    internal sealed class TerrainLayerLegend : MonoBehaviour
    {
        private const float WidePanelWidth = 174f;
        private const float NarrowPanelWidth = 148f;
        private const float HorizontalMargin = 5f;
        private const float TopReserve = 72f;
        private const float BottomReserve = 76f;
        private const int GradientResolution = 256;
        private const string ContinuousFrameSprite =
            "terrainlab/legend/scale_continuous";
        private const string CategoricalFrameSprite =
            "terrainlab/legend/scale_categorical";
        private const string TopCapSprite =
            "terrainlab/legend/panel_top";

        private static readonly Color PanelInterior =
            new Color(0.20f, 0.22f, 0.18f, 0.98f);
        private static readonly Color TextColor =
            new Color(0.88f, 0.84f, 0.71f, 1f);
        private static readonly Color MutedTextColor =
            new Color(0.72f, 0.7f, 0.62f, 1f);

        private readonly List<Image> _nativeFrameImages = new List<Image>();

        private GameObject _root;
        private RectTransform _rootRect;
        private RectTransform _bodyHost;
        private Image _background;
        private Image _topCap;
        private Image _bottomCap;
        private Text _title;
        private LocalizedText _localizedTitle;
        private Text _minimumText;
        private Text _maximumText;
        private Text _zeroText;
        private RectTransform _zeroMarker;
        private Texture2D _gradientTexture;
        private Sprite _gradientSprite;
        private TerrainLayerLegendDescriptor _descriptor;
        private string _descriptorSignature;
        private string _lastUnit;
        private float _preferredHeight;
        private bool _nativeStyleApplied;

        public void Initialize(Transform canvas)
        {
            if (_root != null || canvas == null)
            {
                return;
            }

            _root = new GameObject(
                "TerrainLabLayerLegend",
                typeof(RectTransform),
                typeof(Image));
            _root.transform.SetParent(canvas, false);
            _rootRect = _root.GetComponent<RectTransform>();
            _rootRect.anchorMin = new Vector2(0f, 0.5f);
            _rootRect.anchorMax = new Vector2(0f, 0.5f);
            _rootRect.pivot = new Vector2(0f, 0.5f);
            _rootRect.anchoredPosition = new Vector2(HorizontalMargin, 0f);
            _rootRect.sizeDelta = new Vector2(WidePanelWidth, 240f);

            _background = _root.GetComponent<Image>();
            _background.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            _background.type = Image.Type.Sliced;
            _background.color = new Color(0.25f, 0.25f, 0.2f, 0.98f);
            _background.raycastTarget = false;
            _nativeFrameImages.Add(_background);

            GameObject interiorObject = new GameObject(
                "TerrainLabLayerLegendInterior",
                typeof(RectTransform),
                typeof(Image));
            interiorObject.transform.SetParent(_root.transform, false);
            RectTransform interior =
                interiorObject.GetComponent<RectTransform>();
            interior.anchorMin = Vector2.zero;
            interior.anchorMax = Vector2.one;
            interior.offsetMin = new Vector2(5f, 7f);
            interior.offsetMax = new Vector2(-5f, -7f);
            Image interiorImage = interiorObject.GetComponent<Image>();
            interiorImage.color = PanelInterior;
            interiorImage.raycastTarget = false;

            _topCap = CreateVerticalFrameCap(_root.transform, true);
            _bottomCap = CreateVerticalFrameCap(_root.transform, false);

            _title = CreateText(
                interior,
                "TerrainLabLayerLegendTitle",
                11,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            RectTransform titleRect = _title.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = new Vector2(0f, 27f);
            titleRect.offsetMin = new Vector2(4f, titleRect.offsetMin.y);
            titleRect.offsetMax = new Vector2(-4f, titleRect.offsetMax.y);
            _title.resizeTextForBestFit = true;
            _title.resizeTextMinSize = 8;
            _title.resizeTextMaxSize = 11;
            _localizedTitle = _title.gameObject.AddComponent<LocalizedText>();
            _localizedTitle.autoField = false;

            GameObject bodyObject = new GameObject(
                "TerrainLabLayerLegendBody",
                typeof(RectTransform));
            bodyObject.transform.SetParent(interior, false);
            _bodyHost = bodyObject.GetComponent<RectTransform>();
            _bodyHost.anchorMin = Vector2.zero;
            _bodyHost.anchorMax = Vector2.one;
            _bodyHost.offsetMin = new Vector2(5f, 5f);
            _bodyHost.offsetMax = new Vector2(-5f, -29f);

            _root.SetActive(false);
            _root.transform.SetAsLastSibling();
        }

        public void Show(TerrainLayerLegendDescriptor descriptor)
        {
            if (_root == null)
            {
                return;
            }

            if (descriptor == null)
            {
                Hide();
                return;
            }

            string signature = descriptor.Signature;
            _descriptor = descriptor;
            if (string.Equals(
                    _descriptorSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                return;
            }

            _descriptorSignature = signature;
            _localizedTitle.setKeyAndUpdate(descriptor.TitleKey);
            RebuildBody();
            _nativeStyleApplied = false;
        }

        public void Tick(bool visible)
        {
            if (_root == null)
            {
                return;
            }

            bool shouldShow = visible && _descriptor != null;
            if (_root.activeSelf != shouldShow)
            {
                _root.SetActive(shouldShow);
            }

            if (!shouldShow)
            {
                return;
            }

            ApplyAdaptiveLayout();
            TryApplyNativeFrameStyle();
            UpdateQuantitativeLabels(false);
        }

        public void Hide()
        {
            _descriptor = null;
            _descriptorSignature = null;
            if (_root != null)
            {
                _root.SetActive(false);
            }

            ClearBody();
        }

        private void RebuildBody()
        {
            ClearBody();
            if (_descriptor.Kind == TerrainLayerLegendKind.Continuous)
            {
                BuildContinuousLegend();
                _preferredHeight = 248f;
            }
            else
            {
                BuildCategoricalLegend();
                _preferredHeight = Mathf.Clamp(
                    48f + _descriptor.Entries.Count * 25f,
                    116f,
                    330f);
            }
        }

        private void BuildContinuousLegend()
        {
            GameObject frameObject = new GameObject(
                "TerrainLabLegendScaleFrame",
                typeof(RectTransform),
                typeof(Image));
            frameObject.transform.SetParent(_bodyHost, false);
            RectTransform frameRect = frameObject.GetComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0f, 0f);
            frameRect.anchorMax = new Vector2(0f, 1f);
            frameRect.pivot = new Vector2(0f, 0.5f);
            frameRect.anchoredPosition = Vector2.zero;
            frameRect.sizeDelta = new Vector2(35f, 0f);
            Image frame = frameObject.GetComponent<Image>();
            Sprite customFrame = LoadLegendSprite(ContinuousFrameSprite);
            frame.sprite = customFrame ??
                SpriteTextureLoader.getSprite(
                    "ui/special/darkInputFieldEmpty");
            frame.type = customFrame == null
                ? Image.Type.Sliced
                : Image.Type.Simple;
            frame.color = Color.white;
            frame.raycastTarget = false;
            if (customFrame == null)
            {
                _nativeFrameImages.Add(frame);
            }

            _gradientTexture = new Texture2D(
                1,
                GradientResolution,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "TerrainLabLegend_" + _descriptor.Id,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color32[] colors = new Color32[GradientResolution];
            for (int index = 0; index < colors.Length; index++)
            {
                colors[index] = _descriptor.Sample(
                    index / (float)(colors.Length - 1));
            }

            _gradientTexture.SetPixels32(colors);
            _gradientTexture.Apply(false, true);
            _gradientSprite = Sprite.Create(
                _gradientTexture,
                new Rect(0f, 0f, 1f, GradientResolution),
                new Vector2(0.5f, 0.5f),
                1f);
            _gradientSprite.name = _gradientTexture.name + "_Sprite";
            _gradientSprite.hideFlags = HideFlags.HideAndDontSave;

            GameObject scaleObject = new GameObject(
                "TerrainLabLegendContinuousScale",
                typeof(RectTransform),
                typeof(Image));
            scaleObject.transform.SetParent(frameObject.transform, false);
            RectTransform scaleRect = scaleObject.GetComponent<RectTransform>();
            scaleRect.anchorMin = Vector2.zero;
            scaleRect.anchorMax = Vector2.one;
            scaleRect.offsetMin = new Vector2(7f, 7f);
            scaleRect.offsetMax = new Vector2(-7f, -7f);
            Image scale = scaleObject.GetComponent<Image>();
            scale.sprite = _gradientSprite;
            scale.type = Image.Type.Simple;
            scale.raycastTarget = false;

            _maximumText = CreateValueText(
                _bodyHost,
                "TerrainLabLegendMaximum",
                new Vector2(42f, -1f),
                new Vector2(-1f, -35f),
                TextAnchor.UpperLeft);
            _minimumText = CreateValueText(
                _bodyHost,
                "TerrainLabLegendMinimum",
                new Vector2(42f, 35f),
                new Vector2(-1f, 1f),
                TextAnchor.LowerLeft);

            if (_descriptor.IncludesZero)
            {
                float ratio = (float)(
                    -_descriptor.Minimum /
                    (_descriptor.Maximum - _descriptor.Minimum));
                GameObject markerObject = new GameObject(
                    "TerrainLabLegendZeroMarker",
                    typeof(RectTransform),
                    typeof(Image));
                markerObject.transform.SetParent(_bodyHost, false);
                _zeroMarker = markerObject.GetComponent<RectTransform>();
                _zeroMarker.anchorMin = new Vector2(0f, ratio);
                _zeroMarker.anchorMax = new Vector2(0f, ratio);
                _zeroMarker.pivot = new Vector2(0f, 0.5f);
                _zeroMarker.anchoredPosition = new Vector2(31f, 0f);
                _zeroMarker.sizeDelta = new Vector2(9f, 1f);
                Image marker = markerObject.GetComponent<Image>();
                marker.color = new Color(0.96f, 0.9f, 0.7f, 0.92f);
                marker.raycastTarget = false;

                _zeroText = CreateText(
                    _bodyHost,
                    "TerrainLabLegendZero",
                    9,
                    FontStyle.Normal,
                    TextAnchor.MiddleLeft,
                    MutedTextColor);
                RectTransform zeroRect =
                    _zeroText.GetComponent<RectTransform>();
                zeroRect.anchorMin = new Vector2(0f, ratio);
                zeroRect.anchorMax = new Vector2(1f, ratio);
                zeroRect.pivot = new Vector2(0f, 0.5f);
                zeroRect.anchoredPosition = new Vector2(42f, 0f);
                zeroRect.sizeDelta = new Vector2(-43f, 18f);
            }

            UpdateQuantitativeLabels(true);
        }

        private void BuildCategoricalLegend()
        {
            GameObject viewportObject = new GameObject(
                "TerrainLabLegendCategoryViewport",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D));
            viewportObject.transform.SetParent(_bodyHost, false);
            RectTransform viewport =
                viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            Image viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
            viewportImage.raycastTarget = true;

            GameObject contentObject = new GameObject(
                "TerrainLabLegendCategoryContent",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            contentObject.transform.SetParent(viewportObject.transform, false);
            RectTransform content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            VerticalLayoutGroup layout =
                contentObject.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(1, 1, 1, 1);
            layout.spacing = 3f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter =
                contentObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            ScrollRect scroll = viewportObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 18f;

            for (int index = 0; index < _descriptor.Entries.Count; index++)
            {
                CreateCategoryRow(content, _descriptor.Entries[index]);
            }
        }

        private void CreateCategoryRow(
            Transform parent,
            TerrainLayerLegendEntry entry)
        {
            GameObject rowObject = new GameObject(
                "TerrainLabLegendCategory_" + entry.Id,
                typeof(RectTransform),
                typeof(LayoutElement));
            rowObject.transform.SetParent(parent, false);
            RectTransform row = rowObject.GetComponent<RectTransform>();
            row.sizeDelta = new Vector2(0f, 22f);
            LayoutElement rowLayout = rowObject.GetComponent<LayoutElement>();
            rowLayout.preferredHeight = 22f;
            rowLayout.minHeight = 22f;
            rowLayout.flexibleWidth = 1f;

            GameObject frameObject = new GameObject(
                "TerrainLabLegendCategoryFrame",
                typeof(RectTransform),
                typeof(Image));
            frameObject.transform.SetParent(rowObject.transform, false);
            RectTransform frameRect = frameObject.GetComponent<RectTransform>();
            frameRect.anchorMin = new Vector2(0f, 0.5f);
            frameRect.anchorMax = new Vector2(0f, 0.5f);
            frameRect.pivot = new Vector2(0f, 0.5f);
            frameRect.anchoredPosition = Vector2.zero;
            frameRect.sizeDelta = new Vector2(54f, 21f);
            Image frame = frameObject.GetComponent<Image>();
            Sprite customFrame = LoadLegendSprite(CategoricalFrameSprite);
            frame.sprite = customFrame ??
                SpriteTextureLoader.getSprite(
                    "ui/special/darkInputFieldEmpty");
            frame.type = customFrame == null
                ? Image.Type.Sliced
                : Image.Type.Simple;
            frame.color = Color.white;
            frame.raycastTarget = false;
            if (customFrame == null)
            {
                _nativeFrameImages.Add(frame);
            }

            GameObject maskObject = new GameObject(
                "TerrainLabLegendCategoryMask",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D));
            maskObject.transform.SetParent(frameObject.transform, false);
            RectTransform maskRect = maskObject.GetComponent<RectTransform>();
            maskRect.anchorMin = Vector2.zero;
            maskRect.anchorMax = Vector2.one;
            maskRect.offsetMin = new Vector2(7f, 4f);
            maskRect.offsetMax = new Vector2(-7f, -4f);
            Image maskImage = maskObject.GetComponent<Image>();
            maskImage.color = entry.LineSymbol
                ? new Color(0.03f, 0.03f, 0.025f, 1f)
                : (Color)entry.Color;
            maskImage.raycastTarget = false;

            if (entry.LineSymbol)
            {
                GameObject lineObject = new GameObject(
                    "TerrainLabLegendLineSymbol",
                    typeof(RectTransform),
                    typeof(Image));
                lineObject.transform.SetParent(maskObject.transform, false);
                RectTransform lineRect =
                    lineObject.GetComponent<RectTransform>();
                lineRect.anchorMin = new Vector2(0f, 0.5f);
                lineRect.anchorMax = new Vector2(1f, 0.5f);
                lineRect.pivot = new Vector2(0.5f, 0.5f);
                lineRect.sizeDelta = new Vector2(0f, 3f);
                Image line = lineObject.GetComponent<Image>();
                line.color = entry.Color;
                line.raycastTarget = false;
            }
            else if (entry.Sprite != null)
            {
                const int repeats = 4;
                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    GameObject spriteObject = new GameObject(
                        "TerrainLabLegendBiotopeTile_" + repeat,
                        typeof(RectTransform),
                        typeof(Image));
                    spriteObject.transform.SetParent(maskObject.transform, false);
                    RectTransform spriteRect =
                        spriteObject.GetComponent<RectTransform>();
                    spriteRect.anchorMin = new Vector2(
                        repeat / (float)repeats,
                        0f);
                    spriteRect.anchorMax = new Vector2(
                        (repeat + 1f) / repeats,
                        1f);
                    spriteRect.offsetMin = Vector2.zero;
                    spriteRect.offsetMax = Vector2.zero;
                    Image spriteImage = spriteObject.GetComponent<Image>();
                    spriteImage.sprite = entry.Sprite;
                    spriteImage.type = Image.Type.Simple;
                    spriteImage.preserveAspect = false;
                    spriteImage.raycastTarget = false;
                }
            }

            Text label = CreateText(
                rowObject.transform,
                "TerrainLabLegendCategoryLabel",
                9,
                FontStyle.Normal,
                TextAnchor.MiddleLeft,
                TextColor);
            RectTransform labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(60f, 0f);
            labelRect.offsetMax = new Vector2(-1f, 0f);
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 9;
            LocalizedText localized =
                label.gameObject.AddComponent<LocalizedText>();
            localized.autoField = false;
            localized.setKeyAndUpdate(entry.LabelKey);
        }

        private Image CreateVerticalFrameCap(Transform parent, bool top)
        {
            GameObject capObject = new GameObject(
                top
                    ? "TerrainLabLayerLegendTopCap"
                    : "TerrainLabLayerLegendBottomCap",
                typeof(RectTransform),
                typeof(Image));
            capObject.transform.SetParent(parent, false);
            RectTransform rect = capObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, top ? 1f : 0f);
            rect.anchorMax = rect.anchorMin;
            // The lower copy flips around its bottom edge so it grows outside
            // the panel instead of covering the legend body.
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(42f, 58f);

            Image image = capObject.GetComponent<Image>();
            image.sprite = LoadLegendSprite(TopCapSprite);
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            image.color = Color.white;
            image.raycastTarget = false;
            if (!top)
            {
                rect.localScale = new Vector3(1f, -1f, 1f);
            }
            image.gameObject.SetActive(image.sprite != null);
            return image;
        }

        private static Sprite LoadLegendSprite(string path)
        {
            try
            {
                Sprite sprite = SpriteTextureLoader.getSprite(path);
                if (sprite != null && sprite.texture != null)
                {
                    sprite.texture.filterMode = FilterMode.Point;
                    sprite.texture.wrapMode = TextureWrapMode.Clamp;
                }

                return sprite;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private Text CreateValueText(
            Transform parent,
            string name,
            Vector2 offsetMin,
            Vector2 offsetMax,
            TextAnchor alignment)
        {
            Text text = CreateText(
                parent,
                name,
                9,
                FontStyle.Bold,
                alignment,
                TextColor);
            RectTransform rect = text.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 7;
            text.resizeTextMaxSize = 9;
            return text;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            int fontSize,
            FontStyle fontStyle,
            TextAnchor alignment,
            Color color)
        {
            GameObject textObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(Text));
            textObject.transform.SetParent(parent, false);
            Text text = textObject.GetComponent<Text>();
            text.font = LocalizedTextManager.current_font;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private void UpdateQuantitativeLabels(bool force)
        {
            if (_descriptor?.Kind != TerrainLayerLegendKind.Continuous ||
                _minimumText == null || _maximumText == null)
            {
                return;
            }

            string unit = string.IsNullOrEmpty(_descriptor.UnitKey)
                ? string.Empty
                : LM.Get(_descriptor.UnitKey);
            if (!force && string.Equals(
                    unit,
                    _lastUnit,
                    StringComparison.Ordinal))
            {
                return;
            }

            _lastUnit = unit;
            _maximumText.text = string.Format(
                LM.Get("terrain_lab_legend_maximum_format"),
                FormatNumber(_descriptor.Maximum),
                unit);
            _minimumText.text = string.Format(
                LM.Get("terrain_lab_legend_minimum_format"),
                FormatNumber(_descriptor.Minimum),
                unit);
            if (_zeroText != null)
            {
                _zeroText.text = string.Format(
                    LM.Get("terrain_lab_legend_zero_format"),
                    unit);
            }
        }

        private static string FormatNumber(double value)
        {
            double rounded = Math.Round(value);
            if (Math.Abs(value - rounded) < 0.005d)
            {
                return rounded.ToString("0", CultureInfo.InvariantCulture);
            }

            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void ApplyAdaptiveLayout()
        {
            RectTransform canvas = _rootRect.parent as RectTransform;
            if (canvas == null)
            {
                return;
            }

            float width = canvas.rect.width < 520f
                ? NarrowPanelWidth
                : WidePanelWidth;
            float availableHeight = Mathf.Max(
                112f,
                canvas.rect.height - TopReserve - BottomReserve);
            float height = Mathf.Min(_preferredHeight, availableHeight);
            _rootRect.sizeDelta = new Vector2(width, height);
            _rootRect.anchoredPosition = new Vector2(
                HorizontalMargin,
                (BottomReserve - TopReserve) * 0.5f);
        }

        private void TryApplyNativeFrameStyle()
        {
            if (_nativeStyleApplied || ToolbarButtons.instance == null)
            {
                return;
            }

            Image stockBackground = ToolbarButtons.instance.main_background;
            Sprite stockButton = ToolbarButtons.getSpriteButtonNormal();
            if (stockBackground == null || stockBackground.sprite == null ||
                stockButton == null)
            {
                return;
            }

            for (int index = 0; index < _nativeFrameImages.Count; index++)
            {
                Image target = _nativeFrameImages[index];
                if (target == null)
                {
                    continue;
                }
                if (ReferenceEquals(target, _topCap) ||
                    ReferenceEquals(target, _bottomCap))
                {
                    continue;
                }

                bool panelPart = ReferenceEquals(target, _background);
                target.sprite = panelPart
                    ? stockBackground.sprite
                    : stockButton;
                target.type = Image.Type.Sliced;
                target.material = panelPart
                    ? stockBackground.material
                    : null;
                target.color = panelPart
                    ? stockBackground.color
                    : Color.white;
                target.pixelsPerUnitMultiplier =
                    stockBackground.pixelsPerUnitMultiplier;
            }

            _nativeStyleApplied = true;
        }

        private void ClearBody()
        {
            if (_bodyHost != null)
            {
                for (int index = _bodyHost.childCount - 1; index >= 0; index--)
                {
                    Destroy(_bodyHost.GetChild(index).gameObject);
                }
            }

            for (int index = _nativeFrameImages.Count - 1; index >= 0; index--)
            {
                Image image = _nativeFrameImages[index];
                if (image != null &&
                    !ReferenceEquals(image, _background) &&
                    !ReferenceEquals(image, _topCap) &&
                    !ReferenceEquals(image, _bottomCap))
                {
                    _nativeFrameImages.RemoveAt(index);
                }
            }

            _minimumText = null;
            _maximumText = null;
            _zeroText = null;
            _zeroMarker = null;
            _lastUnit = null;
            if (_gradientSprite != null)
            {
                Destroy(_gradientSprite);
                _gradientSprite = null;
            }

            if (_gradientTexture != null)
            {
                Destroy(_gradientTexture);
                _gradientTexture = null;
            }
        }

        private void OnDestroy()
        {
            ClearBody();
            if (_root != null)
            {
                Destroy(_root);
                _root = null;
            }
        }
    }
}
