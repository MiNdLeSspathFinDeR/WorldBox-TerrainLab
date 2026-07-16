using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TerrainLab
{
    public static class TerrainElevationEncoding
    {
        public const short Minimum = -20000;
        public const short Maximum = 9000;
        public const short NoData = 9999;
        public const int WorldBoxMinimum = 0;
        public const int WorldBoxSeaLevel = 98;
        public const int WorldBoxMaximum = 255;

        public static bool IsDataValue(long value)
        {
            return value >= Minimum && value <= Maximum;
        }

        public static bool IsStoredValue(short value)
        {
            return value == NoData || IsDataValue(value);
        }

        public static short FromWorldHeight(int worldHeight)
        {
            int height = Math.Max(
                WorldBoxMinimum,
                Math.Min(WorldBoxMaximum, worldHeight));
            if (height <= WorldBoxSeaLevel)
            {
                double ratio = height / (double)WorldBoxSeaLevel;
                return (short)Math.Round(Minimum + ratio * -Minimum);
            }

            double landRatio = (height - WorldBoxSeaLevel) /
                               (double)(WorldBoxMaximum - WorldBoxSeaLevel);
            return (short)Math.Round(landRatio * Maximum);
        }

        public static int ToWorldHeight(short elevation)
        {
            if (!IsDataValue(elevation))
            {
                throw new ArgumentOutOfRangeException(nameof(elevation));
            }

            if (elevation <= 0)
            {
                double ratio = (elevation - (double)Minimum) / -Minimum;
                return (int)Math.Round(WorldBoxMinimum +
                    ratio * (WorldBoxSeaLevel - WorldBoxMinimum));
            }

            double landRatio = elevation / (double)Maximum;
            return (int)Math.Round(WorldBoxSeaLevel +
                landRatio * (WorldBoxMaximum - WorldBoxSeaLevel));
        }
    }

    public enum TerrainLandform : byte
    {
        Unknown = 0,
        Plain = 1,
        Lowland = 2,
        Upland = 3,
        Hill = 4,
        Mountain = 5,
        Summit = 6,
        Channel = 7,
        Depression = 8,
        Cliff = 9,
        Artificial = 10
    }

    public enum TerrainMaterial : byte
    {
        Unknown = 0,
        Soil = 1,
        Sand = 2,
        Rock = 3,
        Ice = 4,
        Lava = 5,
        Organic = 6,
        Artificial = 7
    }

    public enum TerrainElevationOperation
    {
        Set,
        Raise,
        Lower,
        Smooth
    }

    public sealed class TerrainElevationEdit : ITerrainLabEdit
    {
        internal TerrainElevationEdit(
            string projectId,
            int[] indices,
            short[] before,
            short[] after,
            int[] worldCacheBefore)
        {
            ProjectId = projectId;
            Indices = indices;
            Before = before;
            After = after;
            WorldCacheBefore = worldCacheBefore;
        }

        public string ProjectId { get; }

        public int ChangedCellCount => Indices.Length;

        public long EstimatedBytes =>
            (long)Indices.Length * sizeof(int) +
            (long)Before.Length * sizeof(short) +
            (long)After.Length * sizeof(short) +
            (long)(WorldCacheBefore?.Length ?? 0) * sizeof(int) + 96L;

        internal int[] Indices { get; }

        internal short[] Before { get; }

        internal short[] After { get; }

        internal int[] WorldCacheBefore { get; }

        public void Apply(TerrainWorldState state, bool useAfterValues)
        {
            state.ApplyElevationEdit(this, useAfterValues);
        }
    }

    public sealed class TerrainWorldState
    {
        private static readonly FieldInfo TilesListField =
            AccessTools.Field(typeof(MapBox), "tiles_list");
        private static readonly MethodInfo CityPlaceFinderSetDirtyMethod =
            AccessTools.Method(typeof(CityPlaceFinder), "setDirty");

        public string ProjectId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public short SeaLevel { get; private set; }

        public short[] Elevation { get; private set; }

        public byte[] Landform { get; private set; }

        public byte[] Material { get; private set; }

        public int CellCount => Elevation?.Length ?? 0;

        public long Revision { get; private set; }

        public TerrainHydrologyResult Hydrology { get; internal set; }

        public TerrainWaterState WaterDynamics { get; internal set; }

        public TerrainReliefResult Relief { get; internal set; }

        public TerrainErosionResult Erosion { get; internal set; }

        public bool IsDirty { get; private set; }

        public TerrainWaterState EnsureWaterDynamics()
        {
            if (WaterDynamics == null)
            {
                WaterDynamics = TerrainWaterState.Create(CellCount);
            }

            WaterDynamics.ValidateAndRecount(CellCount);
            return WaterDynamics;
        }

        private TerrainWorldState()
        {
        }

        public static TerrainWorldState CaptureCurrentWorld()
        {
            int width = MapBox.width;
            int height = MapBox.height;
            if (!TerrainMapLimits.TryValidate(width, height, out string error))
            {
                throw new InvalidOperationException(error);
            }

            WorldTile[] tiles = GetCurrentTiles();
            int expected = checked(width * height);
            if (tiles == null || tiles.Length != expected)
            {
                throw new InvalidOperationException("World tile array does not match map dimensions.");
            }

            TerrainWorldState state = CreateEmpty(
                Guid.NewGuid().ToString("D"),
                DateTime.UtcNow,
                width,
                height,
                0);

            for (int index = 0; index < tiles.Length; index++)
            {
                WorldTile tile = tiles[index];
                state.ClassifyTile(index, tile);
                state.Elevation[index] = (short)Math.Max(
                    TerrainElevationEncoding.WorldBoxMinimum,
                    Math.Min(TerrainElevationEncoding.WorldBoxMaximum, tile.Height));
            }

            TerrainEarthElevationModel.InferWorldElevations(
                tiles,
                state.Landform,
                state.Elevation);

            return state;
        }

        public static TerrainWorldState CreateFromLayers(
            string projectId,
            DateTime createdUtc,
            int width,
            int height,
            short seaLevel,
            short[] elevation,
            byte[] landform,
            byte[] material)
        {
            if (!TerrainMapLimits.TryValidate(width, height, out string error))
            {
                throw new InvalidOperationException(error);
            }

            if (!TerrainElevationEncoding.IsDataValue(seaLevel))
            {
                throw new InvalidOperationException(
                    "Sea level must be between -20000 and 9000 metres.");
            }

            int expected = checked(width * height);
            if (elevation == null || elevation.Length != expected ||
                landform == null || landform.Length != expected ||
                material == null || material.Length != expected)
            {
                throw new InvalidOperationException("WBXGEO core layers have inconsistent dimensions.");
            }

            short[] normalizedElevation = (short[])elevation.Clone();
            bool legacyWorldHeightCache = seaLevel ==
                TerrainElevationEncoding.WorldBoxSeaLevel;
            if (legacyWorldHeightCache)
            {
                for (int index = 0; index < normalizedElevation.Length; index++)
                {
                    short value = normalizedElevation[index];
                    if (value != TerrainElevationEncoding.NoData &&
                        (value < TerrainElevationEncoding.WorldBoxMinimum ||
                         value > TerrainElevationEncoding.WorldBoxMaximum))
                    {
                        legacyWorldHeightCache = false;
                        break;
                    }
                }
            }

            if (legacyWorldHeightCache)
            {
                for (int index = 0; index < normalizedElevation.Length; index++)
                {
                    if (normalizedElevation[index] != TerrainElevationEncoding.NoData)
                    {
                        normalizedElevation[index] =
                            TerrainElevationEncoding.FromWorldHeight(
                                normalizedElevation[index]);
                    }
                }
            }
            else if (seaLevel != 0)
            {
                for (int index = 0; index < normalizedElevation.Length; index++)
                {
                    if (normalizedElevation[index] == TerrainElevationEncoding.NoData)
                    {
                        continue;
                    }

                    long shifted = (long)normalizedElevation[index] - seaLevel;
                    if (!TerrainElevationEncoding.IsDataValue(shifted))
                    {
                        throw new InvalidOperationException(
                            "Elevation shifted to the zero sea-level datum is outside -20000..9000 metres.");
                    }

                    normalizedElevation[index] = (short)shifted;
                }
            }

            for (int index = 0; index < normalizedElevation.Length; index++)
            {
                if (!TerrainElevationEncoding.IsStoredValue(normalizedElevation[index]))
                {
                    throw new InvalidOperationException(
                        "Elevation must be NODATA or between -20000 and 9000 metres.");
                }
            }

            return new TerrainWorldState
            {
                ProjectId = string.IsNullOrWhiteSpace(projectId)
                    ? Guid.NewGuid().ToString("D")
                    : projectId,
                CreatedUtc = createdUtc == default(DateTime) ? DateTime.UtcNow : createdUtc,
                Width = width,
                Height = height,
                SeaLevel = 0,
                Elevation = normalizedElevation,
                Landform = landform,
                Material = material
            };
        }

        public bool MatchesCurrentWorld()
        {
            return Width == MapBox.width &&
                   Height == MapBox.height &&
                   GetCurrentTiles() != null &&
                   GetCurrentTiles().Length == CellCount;
        }

        public void ApplyElevationToWorldCache()
        {
            if (!MatchesCurrentWorld())
            {
                throw new InvalidOperationException("Terrain state does not match the loaded world.");
            }

            WorldTile[] tiles = GetCurrentTiles();
            for (int index = 0; index < tiles.Length; index++)
            {
                short value = Elevation[index];
                if (value == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                tiles[index].Height = TerrainElevationEncoding.ToWorldHeight(value);
            }
        }

        public bool TryGetElevation(int x, int y, out short elevation)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height || Elevation == null)
            {
                elevation = TerrainElevationEncoding.NoData;
                return false;
            }

            elevation = Elevation[checked(y * Width + x)];
            return true;
        }

        public TerrainElevationEdit ApplyElevationBrush(
            int centerX,
            int centerY,
            int radius,
            TerrainElevationOperation operation,
            short targetElevation,
            short step)
        {
            if (centerX < 0 || centerX >= Width || centerY < 0 || centerY >= Height)
            {
                throw new ArgumentOutOfRangeException(nameof(centerX));
            }

            if (radius < 0 || radius > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(radius));
            }

            if (!TerrainElevationEncoding.IsDataValue(targetElevation))
            {
                throw new ArgumentException(
                    "Elevation must be between -20000 and 9000 metres.");
            }

            if (step <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step));
            }

            List<int> candidateIndices = CollectBrushIndices(centerX, centerY, radius);
            List<int> changedIndices = new List<int>(candidateIndices.Count);
            List<short> beforeValues = new List<short>(candidateIndices.Count);
            List<short> afterValues = new List<short>(candidateIndices.Count);

            foreach (int index in candidateIndices)
            {
                short before = Elevation[index];
                if (before == TerrainElevationEncoding.NoData &&
                    operation != TerrainElevationOperation.Set)
                {
                    continue;
                }

                short after;
                switch (operation)
                {
                    case TerrainElevationOperation.Set:
                        after = targetElevation;
                        break;
                    case TerrainElevationOperation.Raise:
                        after = ClampElevation((long)before + step);
                        break;
                    case TerrainElevationOperation.Lower:
                        after = ClampElevation((long)before - step);
                        break;
                    case TerrainElevationOperation.Smooth:
                        after = CalculateSmoothedElevation(index, before);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }

                if (after == before)
                {
                    continue;
                }

                changedIndices.Add(index);
                beforeValues.Add(before);
                afterValues.Add(after);
            }

            TerrainElevationEdit edit = new TerrainElevationEdit(
                ProjectId,
                changedIndices.ToArray(),
                beforeValues.ToArray(),
                afterValues.ToArray(),
                CaptureWorldCache(changedIndices));
            ApplyElevationEdit(edit, true);
            return edit;
        }

        public TerrainElevationEdit ApplyElevationGrid(short[] values)
        {
            if (values == null || values.Length != CellCount)
            {
                throw new ArgumentException(
                    "Replacement elevation grid does not match the project.",
                    nameof(values));
            }

            for (int index = 0; index < values.Length; index++)
            {
                if (!TerrainElevationEncoding.IsStoredValue(values[index]))
                {
                    throw new ArgumentException(
                        "Replacement elevation must be NODATA or between -20000 and 9000 metres.",
                        nameof(values));
                }
            }

            int changedCount = 0;
            for (int index = 0; index < values.Length; index++)
            {
                if (Elevation[index] != values[index])
                {
                    changedCount++;
                }
            }

            int[] changedIndices = new int[changedCount];
            short[] beforeValues = new short[changedCount];
            short[] afterValues = new short[changedCount];
            int offset = 0;
            for (int index = 0; index < values.Length; index++)
            {
                short before = Elevation[index];
                short after = values[index];
                if (before == after)
                {
                    continue;
                }

                changedIndices[offset] = index;
                beforeValues[offset] = before;
                afterValues[offset] = after;
                offset++;
            }

            TerrainElevationEdit edit = new TerrainElevationEdit(
                ProjectId,
                changedIndices,
                beforeValues,
                afterValues,
                CaptureWorldCache(changedIndices));
            ApplyElevationEdit(edit, true);
            return edit;
        }

        public void ApplyElevationEdit(TerrainElevationEdit edit, bool useAfterValues)
        {
            if (edit == null)
            {
                throw new ArgumentNullException(nameof(edit));
            }

            if (!string.Equals(edit.ProjectId, ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Elevation edit belongs to another project.");
            }

            short[] source = useAfterValues ? edit.After : edit.Before;
            for (int offset = 0; offset < edit.Indices.Length; offset++)
            {
                int index = edit.Indices[offset];
                if (index < 0 || index >= CellCount)
                {
                    throw new InvalidOperationException("Elevation edit index is outside the project grid.");
                }

                Elevation[index] = source[offset];
            }

            if (edit.ChangedCellCount > 0)
            {
                Revision++;
                IsDirty = true;
                ApplyElevationEditToWorldCache(edit, source);
            }
        }

        internal bool TryGetSurfaceStamp(
            int x,
            int y,
            out TerrainSurfaceStamp stamp)
        {
            stamp = default(TerrainSurfaceStamp);
            if (x < 0 || x >= Width || y < 0 || y >= Height ||
                !MatchesCurrentWorld())
            {
                return false;
            }

            WorldTile[] tiles = GetCurrentTiles();
            stamp = TerrainSurfaceStamp.Capture(tiles[checked(y * Width + x)]);
            return true;
        }

        internal int[] CollectConnectedSurfaceRegion(
            int x,
            int y,
            out TerrainSurfaceStamp source)
        {
            if (!TryGetSurfaceStamp(x, y, out source))
            {
                return Array.Empty<int>();
            }

            WorldTile[] tiles = GetCurrentTiles();
            int startIndex = checked(y * Width + x);
            TerrainSurfaceStamp regionSource = source;
            return TerrainDigitizingRaster.CollectConnectedRegion(
                startIndex,
                Width,
                Height,
                index => TerrainSurfaceStamp.Capture(tiles[index]).Equals(regionSource));
        }

        internal TerrainSurfaceEdit ApplySurfaceCells(
            int[] candidateIndices,
            TerrainSurfaceStamp target)
        {
            if (candidateIndices == null)
            {
                throw new ArgumentNullException(nameof(candidateIndices));
            }

            if (!MatchesCurrentWorld())
            {
                throw new InvalidOperationException(
                    "Terrain state does not match the loaded world.");
            }

            if (!target.TryResolve(out _, out _, out string resolveError))
            {
                throw new InvalidOperationException(resolveError);
            }

            WorldTile[] tiles = GetCurrentTiles();
            List<int> changed = new List<int>(candidateIndices.Length);
            List<ushort> beforeCodes = new List<ushort>(candidateIndices.Length);
            List<TerrainSurfaceStamp> palette = new List<TerrainSurfaceStamp>();
            Dictionary<TerrainSurfaceStamp, ushort> paletteLookup =
                new Dictionary<TerrainSurfaceStamp, ushort>();
            for (int offset = 0; offset < candidateIndices.Length; offset++)
            {
                int index = candidateIndices[offset];
                if (index < 0 || index >= CellCount)
                {
                    continue;
                }

                TerrainSurfaceStamp before = TerrainSurfaceStamp.Capture(tiles[index]);
                if (before.Equals(target))
                {
                    continue;
                }

                if (!paletteLookup.TryGetValue(before, out ushort code))
                {
                    if (palette.Count == ushort.MaxValue)
                    {
                        throw new InvalidOperationException(
                            "Surface edit contains too many distinct source types.");
                    }

                    code = (ushort)palette.Count;
                    palette.Add(before);
                    paletteLookup.Add(before, code);
                }

                changed.Add(index);
                beforeCodes.Add(code);
            }

            int[] indices = changed.Count == candidateIndices.Length
                ? candidateIndices
                : changed.ToArray();
            TerrainSurfaceEdit edit = new TerrainSurfaceEdit(
                ProjectId,
                indices,
                palette.ToArray(),
                palette.Count <= 1 ? null : beforeCodes.ToArray(),
                target);
            ApplySurfaceEdit(edit, true);
            return edit;
        }

        internal TerrainSurfaceEdit ApplyUniformSurfaceRegion(
            int[] indices,
            TerrainSurfaceStamp before,
            TerrainSurfaceStamp target)
        {
            if (indices == null)
            {
                throw new ArgumentNullException(nameof(indices));
            }

            if (before.Equals(target))
            {
                return new TerrainSurfaceEdit(
                    ProjectId,
                    Array.Empty<int>(),
                    Array.Empty<TerrainSurfaceStamp>(),
                    null,
                    target);
            }

            if (!target.TryResolve(out _, out _, out string resolveError))
            {
                throw new InvalidOperationException(resolveError);
            }

            TerrainSurfaceEdit edit = new TerrainSurfaceEdit(
                ProjectId,
                indices,
                new[] { before },
                null,
                target);
            ApplySurfaceEdit(edit, true);
            return edit;
        }

        public void ApplySurfaceEdit(TerrainSurfaceEdit edit, bool useAfterValues)
        {
            if (edit == null)
            {
                throw new ArgumentNullException(nameof(edit));
            }

            if (!string.Equals(edit.ProjectId, ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Surface edit belongs to another project.");
            }

            if (edit.ChangedCellCount == 0)
            {
                return;
            }

            if (!MatchesCurrentWorld())
            {
                throw new InvalidOperationException(
                    "Terrain state does not match the loaded world.");
            }

            TileType afterMain = null;
            TopTileType afterTop = null;
            TileType[] beforeMain = null;
            TopTileType[] beforeTop = null;
            if (useAfterValues)
            {
                if (!edit.After.TryResolve(
                        out afterMain,
                        out afterTop,
                        out string resolveError))
                {
                    throw new InvalidOperationException(resolveError);
                }
            }
            else
            {
                beforeMain = new TileType[edit.BeforePalette.Length];
                beforeTop = new TopTileType[edit.BeforePalette.Length];
                for (int paletteIndex = 0;
                     paletteIndex < edit.BeforePalette.Length;
                     paletteIndex++)
                {
                    if (!edit.BeforePalette[paletteIndex].TryResolve(
                            out beforeMain[paletteIndex],
                            out beforeTop[paletteIndex],
                            out string resolveError,
                            false))
                    {
                        throw new InvalidOperationException(resolveError);
                    }
                }
            }

            WorldTile[] tiles = GetCurrentTiles();
            bool cityPlacementChanged = false;
            for (int offset = 0; offset < edit.Indices.Length; offset++)
            {
                int index = edit.Indices[offset];
                if (index < 0 || index >= CellCount)
                {
                    throw new InvalidOperationException(
                        "Surface edit index is outside the project grid.");
                }

                TerrainSurfaceStamp stamp;
                TileType mainType;
                TopTileType topType;
                if (useAfterValues)
                {
                    stamp = edit.After;
                    mainType = afterMain;
                    topType = afterTop;
                }
                else
                {
                    int paletteIndex = edit.BeforeCodes == null
                        ? 0
                        : edit.BeforeCodes[offset];
                    stamp = edit.BeforePalette[paletteIndex];
                    mainType = beforeMain[paletteIndex];
                    topType = beforeTop[paletteIndex];
                }

                cityPlacementChanged |= ApplySurfaceToWorldTile(
                    tiles[index],
                    stamp,
                    mainType,
                    topType);
                ClassifyTile(index, tiles[index]);
            }

            if (cityPlacementChanged)
            {
                CityPlaceFinderSetDirtyMethod?.Invoke(
                    World.world.city_zone_helper.city_place_finder,
                    null);
            }

            Revision++;
            IsDirty = true;
            World.world.resetRedrawTimer();
        }

        public void MarkSaved()
        {
            IsDirty = false;
        }

        public void RefreshSemanticsFromWorld()
        {
            if (!MatchesCurrentWorld())
            {
                return;
            }

            WorldTile[] tiles = GetCurrentTiles();
            bool hydrologySourceChanged = false;
            for (int index = 0; index < tiles.Length; index++)
            {
                byte previousLandform = Landform[index];
                ClassifyTile(index, tiles[index]);
                hydrologySourceChanged |= previousLandform != Landform[index];
            }

            if (hydrologySourceChanged)
            {
                Revision++;
            }
        }

        private List<int> CollectBrushIndices(int centerX, int centerY, int radius)
        {
            List<int> indices = new List<int>();
            int radiusSquared = radius * radius;
            for (int offsetY = -radius; offsetY <= radius; offsetY++)
            {
                int y = centerY + offsetY;
                if (y < 0 || y >= Height)
                {
                    continue;
                }

                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    if (offsetX * offsetX + offsetY * offsetY > radiusSquared)
                    {
                        continue;
                    }

                    int x = centerX + offsetX;
                    if (x >= 0 && x < Width)
                    {
                        indices.Add(checked(y * Width + x));
                    }
                }
            }

            return indices;
        }

        private short CalculateSmoothedElevation(int index, short fallback)
        {
            int centerX = index % Width;
            int centerY = index / Width;
            long sum = 0;
            int count = 0;
            for (int y = Math.Max(0, centerY - 1); y <= Math.Min(Height - 1, centerY + 1); y++)
            {
                for (int x = Math.Max(0, centerX - 1); x <= Math.Min(Width - 1, centerX + 1); x++)
                {
                    short value = Elevation[checked(y * Width + x)];
                    if (value == TerrainElevationEncoding.NoData)
                    {
                        continue;
                    }

                    sum += value;
                    count++;
                }
            }

            return count == 0
                ? fallback
                : ClampElevation((long)Math.Round(sum / (double)count));
        }

        private static short ClampElevation(long value)
        {
            return (short)Math.Max(
                TerrainElevationEncoding.Minimum,
                Math.Min(TerrainElevationEncoding.Maximum, value));
        }

        private int[] CaptureWorldCache(IList<int> indices)
        {
            if (!MatchesCurrentWorld())
            {
                return null;
            }

            WorldTile[] tiles = GetCurrentTiles();
            int[] values = new int[indices.Count];
            for (int offset = 0; offset < indices.Count; offset++)
            {
                values[offset] = tiles[indices[offset]].Height;
            }

            return values;
        }

        private void ApplyElevationEditToWorldCache(
            TerrainElevationEdit edit,
            short[] values)
        {
            if (!MatchesCurrentWorld())
            {
                return;
            }

            WorldTile[] tiles = GetCurrentTiles();
            for (int offset = 0; offset < edit.Indices.Length; offset++)
            {
                short value = values[offset];
                if (value != TerrainElevationEncoding.NoData)
                {
                    tiles[edit.Indices[offset]].Height =
                        TerrainElevationEncoding.ToWorldHeight(value);
                }
                else if (edit.WorldCacheBefore != null)
                {
                    tiles[edit.Indices[offset]].Height = edit.WorldCacheBefore[offset];
                }
            }
        }

        private static bool ApplySurfaceToWorldTile(
            WorldTile tile,
            TerrainSurfaceStamp stamp,
            TileType mainType,
            TopTileType topType)
        {
            tile.data.frozen = stamp.Frozen;
            if (tile.hasBuilding())
            {
                MapAction.terraformTile(
                    tile,
                    mainType,
                    topType,
                    TerraformLibrary.nothing);
                return false;
            }

            bool wasFarmable = tile.Type.can_be_farm;
            tile.setTileTypes(mainType, topType, false);
            bool cityPlacementChanged = wasFarmable != tile.Type.can_be_farm &&
                                        tile.zone != null && !tile.zone.hasCity();
            if (tile.burned_stages > 0 && !tile.Type.can_be_set_on_fire)
            {
                tile.removeBurn();
            }

            World.world.setTileDirty(tile);
            return cityPlacementChanged;
        }

        private static TerrainWorldState CreateEmpty(
            string projectId,
            DateTime createdUtc,
            int width,
            int height,
            short seaLevel)
        {
            int count = checked(width * height);
            return new TerrainWorldState
            {
                ProjectId = projectId,
                CreatedUtc = createdUtc,
                Width = width,
                Height = height,
                SeaLevel = seaLevel,
                Elevation = new short[count],
                Landform = new byte[count],
                Material = new byte[count]
            };
        }

        private void ClassifyTile(int index, WorldTile tile)
        {
            string id = tile.main_type?.id ?? tile.Type?.id ?? string.Empty;
            TerrainLandform landform = TerrainLandform.Unknown;
            TerrainMaterial material = TerrainMaterial.Unknown;

            switch (id)
            {
                case "deep_ocean":
                case "close_ocean":
                case "shallow_waters":
                case "pit_deep_ocean":
                case "pit_close_ocean":
                case "pit_shallow_waters":
                    landform = TerrainLandform.Depression;
                    break;
                case "sand":
                    landform = TerrainLandform.Plain;
                    material = TerrainMaterial.Sand;
                    break;
                case "soil_low":
                    landform = TerrainLandform.Lowland;
                    material = TerrainMaterial.Soil;
                    break;
                case "soil_high":
                    landform = TerrainLandform.Upland;
                    material = TerrainMaterial.Soil;
                    break;
                case "hills":
                    landform = TerrainLandform.Hill;
                    material = TerrainMaterial.Rock;
                    break;
                case "mountains":
                    landform = TerrainLandform.Mountain;
                    material = TerrainMaterial.Rock;
                    break;
                case "summit":
                    landform = TerrainLandform.Summit;
                    material = TerrainMaterial.Rock;
                    break;
                default:
                    if (tile.Type.lava)
                    {
                        material = TerrainMaterial.Lava;
                    }
                    else if (tile.Type.rocks)
                    {
                        landform = tile.Type.mountains
                            ? TerrainLandform.Mountain
                            : TerrainLandform.Hill;
                        material = TerrainMaterial.Rock;
                    }
                    else if (tile.Type.ground)
                    {
                        landform = TerrainLandform.Plain;
                        material = TerrainMaterial.Soil;
                    }
                    else
                    {
                        landform = TerrainLandform.Artificial;
                        material = TerrainMaterial.Artificial;
                    }
                    break;
            }

            string visibleId = tile.Type?.id ?? string.Empty;
            if (tile.data.frozen || visibleId.StartsWith("ice", StringComparison.Ordinal) ||
                visibleId.StartsWith("snow", StringComparison.Ordinal) ||
                visibleId.StartsWith("frozen", StringComparison.Ordinal))
            {
                material = TerrainMaterial.Ice;
            }

            Landform[index] = (byte)landform;
            Material[index] = (byte)material;
        }

        private static WorldTile[] GetCurrentTiles()
        {
            if (TilesListField == null || World.world == null)
            {
                return null;
            }

            return (WorldTile[])TilesListField.GetValue(World.world);
        }

        internal WorldTile[] GetCurrentWorldTilesForRuntime()
        {
            WorldTile[] tiles = GetCurrentTiles();
            return tiles != null && tiles.Length == CellCount ? tiles : null;
        }
    }
}
