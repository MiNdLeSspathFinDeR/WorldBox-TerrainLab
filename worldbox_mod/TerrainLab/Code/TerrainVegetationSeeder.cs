using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab
{
    internal sealed class TerrainVegetationSeeder
    {
        internal const string ImportedPlayerName = "ImageToMap";
        internal const string CompletionFlag =
            "terrainlab_initial_vegetation_v2";
        internal const int TilesPerSeed = 48;
        internal const int MaximumSeedCount = 16384;
        internal const int MaximumCandidateCount = MaximumSeedCount * 8;
        internal const int WorkPerFrame = 96;

        private static readonly FieldInfo TilesListField =
            AccessTools.Field(typeof(MapBox), "tiles_list");

        private MapStats _mapStats;
        private WorldTile[] _candidates;
        private int _cursor;
        private int _target;
        private int _spawned;
        private int _attempted;
        private int _eligibleTileCount;
        private int _waitFrames;

        public bool IsPending => _mapStats != null;

        public void Schedule(MapStats mapStats)
        {
            Reset();
            if (!ShouldSeed(mapStats))
            {
                return;
            }

            EnsureCustomData(mapStats);
            if (mapStats.custom_data.hasFlag(CompletionFlag))
            {
                return;
            }

            _mapStats = mapStats;
            _waitFrames = 8;
        }

        public bool Poll()
        {
            if (_mapStats == null)
            {
                return false;
            }

            if (_waitFrames > 0)
            {
                _waitFrames--;
                return false;
            }

            try
            {
                if (_candidates == null)
                {
                    if (!TryPrepare())
                    {
                        _waitFrames = 1;
                        return false;
                    }

                    if (_target == 0)
                    {
                        return Complete();
                    }
                }

                int work = 0;
                while (work < WorkPerFrame &&
                       _cursor < _candidates.Length &&
                       _spawned < _target)
                {
                    WorldTile tile = _candidates[_cursor++];
                    work++;
                    if (!IsEligible(tile))
                    {
                        continue;
                    }

                    if (TrySeedTile(tile, _attempted++))
                    {
                        _spawned++;
                    }
                }

                if (_spawned >= _target ||
                    _cursor >= _candidates.Length)
                {
                    return Complete();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[TerrainLab] Initial vegetation seeding stopped: " +
                    exception.Message);
                Reset();
            }

            return false;
        }

        public void Reset()
        {
            _mapStats = null;
            _candidates = null;
            _cursor = 0;
            _target = 0;
            _spawned = 0;
            _attempted = 0;
            _eligibleTileCount = 0;
            _waitFrames = 0;
        }

        internal static bool ShouldSeed(MapStats mapStats)
        {
            return mapStats != null &&
                   string.Equals(
                       mapStats.player_name,
                       ImportedPlayerName,
                       StringComparison.Ordinal);
        }

        internal static int CalculateSeedTarget(int eligibleTileCount)
        {
            if (eligibleTileCount <= 0)
            {
                return 0;
            }

            return Math.Min(
                MaximumSeedCount,
                Math.Max(3, eligibleTileCount / TilesPerSeed));
        }

        internal static VegetationType GetVegetationType(int index)
        {
            switch (Math.Abs(index % 3))
            {
                case 0:
                    return VegetationType.Trees;
                case 1:
                    return VegetationType.Plants;
                default:
                    return VegetationType.Bushes;
            }
        }

        private bool TryPrepare()
        {
            MapBox world = World.world;
            if (world == null || world.buildings == null ||
                TilesListField == null)
            {
                return false;
            }

            WorldTile[] tiles = TilesListField.GetValue(world) as WorldTile[];
            if (tiles == null || tiles.Length == 0)
            {
                return false;
            }

            int seed = unchecked(
                MapBox.width * 73856093 ^
                MapBox.height * 19349663 ^
                tiles.Length * 83492791);
            System.Random random = new System.Random(seed & int.MaxValue);
            List<WorldTile> eligible =
                new List<WorldTile>(Math.Min(tiles.Length, MaximumCandidateCount));
            foreach (WorldTile tile in tiles)
            {
                if (!IsEligible(tile))
                {
                    continue;
                }

                _eligibleTileCount++;
                if (eligible.Count < MaximumCandidateCount)
                {
                    eligible.Add(tile);
                    continue;
                }

                int replacement = random.Next(_eligibleTileCount);
                if (replacement < MaximumCandidateCount)
                {
                    eligible[replacement] = tile;
                }
            }

            _candidates = eligible.ToArray();
            Shuffle(_candidates, random);
            int desired = CalculateSeedTarget(_eligibleTileCount);
            long missing = desired -
                Math.Max(0L, _mapStats.current_vegetation);
            _target = (int)Math.Max(0L, missing);
            return true;
        }

        private static bool TrySeedTile(WorldTile tile, int rotation)
        {
            for (int offset = 0; offset < 3; offset++)
            {
                BuildingActions.tryGrowVegetationRandom(
                    tile,
                    GetVegetationType(rotation + offset),
                    true,
                    false,
                    false);
                if (tile.hasBuilding())
                {
                    return true;
                }
            }

            return false;
        }

        private bool Complete()
        {
            EnsureCustomData(_mapStats);
            _mapStats.custom_data.addFlag(CompletionFlag);
            Debug.Log(
                "[TerrainLab] Initial living-biome vegetation seeded: " +
                _spawned + " object(s) from " +
                _eligibleTileCount + " eligible tile(s).");
            Reset();
            return true;
        }

        private static bool IsEligible(WorldTile tile)
        {
            TileTypeBase type = tile?.Type;
            BiomeAsset biome = type?.biome_asset;
            return tile != null &&
                   type != null &&
                   type.ground &&
                   biome != null &&
                   biome.grow_vegetation_auto &&
                   !tile.hasBuilding();
        }

        private static void EnsureCustomData(MapStats mapStats)
        {
            if (mapStats.custom_data == null)
            {
                mapStats.custom_data = new SaveCustomData();
            }
        }

        private static void Shuffle(
            WorldTile[] tiles,
            System.Random random)
        {
            for (int index = tiles.Length - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                WorldTile value = tiles[index];
                tiles[index] = tiles[other];
                tiles[other] = value;
            }
        }
    }
}
