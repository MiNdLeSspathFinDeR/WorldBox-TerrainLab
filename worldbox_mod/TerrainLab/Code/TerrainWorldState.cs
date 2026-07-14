using System;
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
