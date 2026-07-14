using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TerrainLab
{
    public static class TerrainElevationEncoding
    {
        public const short NoData = 9999;
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

    public sealed class TerrainElevationEdit
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

        internal int[] Indices { get; }

        internal short[] Before { get; }

        internal short[] After { get; }

        internal int[] WorldCacheBefore { get; }
    }

    public sealed class TerrainWorldState
    {
        private static readonly FieldInfo TilesListField =
            AccessTools.Field(typeof(MapBox), "tiles_list");

        public string ProjectId { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public short SeaLevel { get; set; }

        public short[] Elevation { get; private set; }

        public byte[] Landform { get; private set; }

        public byte[] Material { get; private set; }

        public int CellCount => Elevation?.Length ?? 0;

        public bool IsDirty { get; private set; }

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
                98);

            for (int index = 0; index < tiles.Length; index++)
            {
                WorldTile tile = tiles[index];
                state.Elevation[index] = (short)tile.Height;
                state.ClassifyTile(index, tile);
            }

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

            if (seaLevel == TerrainElevationEncoding.NoData)
            {
                throw new InvalidOperationException("Sea level may not use the reserved NODATA value.");
            }

            int expected = checked(width * height);
            if (elevation == null || elevation.Length != expected ||
                landform == null || landform.Length != expected ||
                material == null || material.Length != expected)
            {
                throw new InvalidOperationException("WBXGEO core layers have inconsistent dimensions.");
            }

            return new TerrainWorldState
            {
                ProjectId = string.IsNullOrWhiteSpace(projectId)
                    ? Guid.NewGuid().ToString("D")
                    : projectId,
                CreatedUtc = createdUtc == default(DateTime) ? DateTime.UtcNow : createdUtc,
                Width = width,
                Height = height,
                SeaLevel = seaLevel,
                Elevation = elevation,
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

                tiles[index].Height = value;
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

            if (targetElevation == TerrainElevationEncoding.NoData)
            {
                throw new ArgumentException("Elevation 9999 is reserved for NODATA.");
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
                        after = ClampElevation((long)before + step, true);
                        break;
                    case TerrainElevationOperation.Lower:
                        after = ClampElevation((long)before - step, false);
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
                IsDirty = true;
                ApplyElevationEditToWorldCache(edit, source);
            }
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
            for (int index = 0; index < tiles.Length; index++)
            {
                ClassifyTile(index, tiles[index]);
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
                : ClampElevation((long)Math.Round(sum / (double)count), false);
        }

        private static short ClampElevation(long value, bool increasing)
        {
            long clamped = Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
            if (clamped == TerrainElevationEncoding.NoData)
            {
                clamped += increasing ? 1 : -1;
            }

            return (short)clamped;
        }

        private int[] CaptureWorldCache(List<int> indices)
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
                    tiles[edit.Indices[offset]].Height = value;
                }
                else if (edit.WorldCacheBefore != null)
                {
                    tiles[edit.Indices[offset]].Height = edit.WorldCacheBefore[offset];
                }
            }
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
    }
}
