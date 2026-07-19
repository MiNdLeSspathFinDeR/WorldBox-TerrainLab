using System;
using System.Collections.Generic;

namespace TerrainLab
{
    public enum TerrainEarthElevationProfile : byte
    {
        Lowland = 0,
        Upland = 1,
        Hill = 2,
        Mountain = 3,
        ShallowWater = 4,
        Shelf = 5,
        DeepOcean = 6
    }

    public static class TerrainEarthElevationModel
    {
        private const int ProfileCount = 7;
        private const int RawHeightCount =
            TerrainElevationEncoding.WorldBoxMaximum + 1;
        private const double ExtremeQuantile = 0.95d;

        public static short GetElevation(
            TerrainEarthElevationProfile profile,
            double quantile)
        {
            double q = Math.Max(0d, Math.Min(1d, quantile));
            double elevation;
            switch (profile)
            {
                case TerrainEarthElevationProfile.Lowland:
                    elevation = Interpolate(
                        q,
                        0d, 0d,
                        0.5d, 350d,
                        0.95d, 1200d,
                        1d, 1800d);
                    break;
                case TerrainEarthElevationProfile.Upland:
                    elevation = Interpolate(
                        q,
                        0d, 300d,
                        0.5d, 900d,
                        0.95d, 1800d,
                        1d, 2400d);
                    break;
                case TerrainEarthElevationProfile.Hill:
                    elevation = Interpolate(
                        q,
                        0d, 600d,
                        0.5d, 1600d,
                        0.95d, 2800d,
                        1d, 3800d);
                    break;
                case TerrainEarthElevationProfile.Mountain:
                    elevation = Interpolate(
                        q,
                        0d, 2200d,
                        0.5d, 4500d,
                        ExtremeQuantile, 7000d,
                        1d, 9000d);
                    break;
                case TerrainEarthElevationProfile.ShallowWater:
                    elevation = -Interpolate(q, 0d, 0d, 1d, 5d);
                    break;
                case TerrainEarthElevationProfile.Shelf:
                    elevation = -Interpolate(q, 0d, 6d, 1d, 149d);
                    break;
                case TerrainEarthElevationProfile.DeepOcean:
                    elevation = -Interpolate(
                        q,
                        0d, 150d,
                        0.5d, 5000d,
                        ExtremeQuantile, 7000d,
                        1d, 11000d);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(profile));
            }

            int rounded = (int)Math.Round(elevation);
            return (short)Math.Max(
                TerrainElevationEncoding.Minimum,
                Math.Min(TerrainElevationEncoding.Maximum, rounded));
        }

        public static short GetRankedElevation(
            TerrainEarthElevationProfile profile,
            int rank,
            int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (rank < 0 || rank >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(rank));
            }

            double ascendingQuantile = (rank + 0.5d) / count;
            int extremeBudget = count / 20;
            if (profile == TerrainEarthElevationProfile.Mountain)
            {
                bool extreme = extremeBudget > 0 && rank >= count - extremeBudget;
                double mountainQuantile = extreme
                    ? Math.Max(ExtremeQuantile, ascendingQuantile)
                    : Math.Min(ExtremeQuantile, ascendingQuantile);
                short mountain = GetElevation(profile, mountainQuantile);
                if (extreme)
                {
                    return (short)Math.Max(7000, (int)mountain);
                }

                return (short)Math.Min(6999, (int)mountain);
            }

            double depthQuantile = 1d - ascendingQuantile;
            if (profile == TerrainEarthElevationProfile.DeepOcean)
            {
                bool extreme = extremeBudget > 0 && rank < extremeBudget;
                double boundedDepthQuantile = extreme
                    ? Math.Max(ExtremeQuantile, depthQuantile)
                    : Math.Min(ExtremeQuantile, depthQuantile);
                short ocean = GetElevation(profile, boundedDepthQuantile);
                if (extreme)
                {
                    return (short)Math.Min(-7000, (int)ocean);
                }

                return (short)Math.Max(-6999, (int)ocean);
            }

            if (profile == TerrainEarthElevationProfile.ShallowWater ||
                profile == TerrainEarthElevationProfile.Shelf)
            {
                return GetElevation(profile, depthQuantile);
            }

            return GetElevation(profile, ascendingQuantile);
        }

        public static short GetBinnedElevation(
            TerrainEarthElevationProfile profile,
            int firstRank,
            int binCount,
            int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (binCount <= 0 || binCount > count)
            {
                throw new ArgumentOutOfRangeException(nameof(binCount));
            }

            if (firstRank < 0 || firstRank > count - binCount)
            {
                throw new ArgumentOutOfRangeException(nameof(firstRank));
            }

            double ascendingQuantile = (firstRank + binCount * 0.5d) / count;
            int extremeBudget = count / 20;
            if (profile == TerrainEarthElevationProfile.Mountain)
            {
                bool extreme = extremeBudget > 0 &&
                    firstRank >= count - extremeBudget;
                short mountain = GetElevation(profile, ascendingQuantile);
                return extreme
                    ? (short)Math.Max(7000, (int)mountain)
                    : (short)Math.Min(6999, (int)mountain);
            }

            double depthQuantile = 1d - ascendingQuantile;
            if (profile == TerrainEarthElevationProfile.DeepOcean)
            {
                bool extreme = extremeBudget > 0 &&
                    firstRank + binCount <= extremeBudget;
                short ocean = GetElevation(profile, depthQuantile);
                return extreme
                    ? (short)Math.Min(-7000, (int)ocean)
                    : (short)Math.Max(-6999, (int)ocean);
            }

            if (profile == TerrainEarthElevationProfile.ShallowWater ||
                profile == TerrainEarthElevationProfile.Shelf)
            {
                return GetElevation(profile, depthQuantile);
            }

            return GetElevation(profile, ascendingQuantile);
        }

        internal static void InferWorldElevations(
            WorldTile[] tiles,
            byte[] landform,
            short[] rawHeightAndElevation,
            int width,
            int height)
        {
            if (tiles == null || landform == null || rawHeightAndElevation == null ||
                tiles.Length != landform.Length ||
                tiles.Length != rawHeightAndElevation.Length ||
                width <= 0 || height <= 0 ||
                checked(width * height) != tiles.Length)
            {
                throw new ArgumentException(
                    "World tiles, landforms, and elevation must have equal dimensions.");
            }

            // Equal vanilla heights share one quantile so flat morphotypes stay flat.
            int[] counts = new int[ProfileCount * RawHeightCount];
            int[] totals = new int[ProfileCount];
            TerrainEarthElevationProfile[] profiles =
                new TerrainEarthElevationProfile[tiles.Length];
            for (int index = 0; index < tiles.Length; index++)
            {
                TerrainEarthElevationProfile profile = GetProfile(
                    tiles[index],
                    (TerrainLandform)landform[index]);
                profiles[index] = profile;
                int rawHeight = GetRankedRawHeight(
                    rawHeightAndElevation[index],
                    (TerrainLandform)landform[index]);
                counts[(int)profile * RawHeightCount + rawHeight]++;
                totals[(int)profile]++;
            }

            int[] prefixes = new int[counts.Length];
            for (int profile = 0; profile < ProfileCount; profile++)
            {
                int offset = profile * RawHeightCount;
                int running = 0;
                for (int rawHeight = 0; rawHeight < RawHeightCount; rawHeight++)
                {
                    prefixes[offset + rawHeight] = running;
                    running += counts[offset + rawHeight];
                }
            }

            for (int index = 0; index < tiles.Length; index++)
            {
                TerrainEarthElevationProfile profile = profiles[index];
                int profileIndex = (int)profile;
                int rawHeight = GetRankedRawHeight(
                    rawHeightAndElevation[index],
                    (TerrainLandform)landform[index]);
                int bin = profileIndex * RawHeightCount + rawHeight;
                rawHeightAndElevation[index] = GetBinnedElevation(
                    profile,
                    prefixes[bin],
                    counts[bin],
                    totals[profileIndex]);
            }

            RegularizeGeneratedSurface(
                width,
                height,
                rawHeightAndElevation,
                profiles);
        }

        public static void RegularizeGeneratedSurface(
            int width,
            int height,
            short[] elevation,
            TerrainEarthElevationProfile[] profiles)
        {
            int count = checked(width * height);
            if (width <= 0 || height <= 0 || elevation == null ||
                profiles == null || elevation.Length != count ||
                profiles.Length != count)
            {
                throw new ArgumentException(
                    "Generated elevation and profile grids must match the canvas.");
            }

            Queue<int> queue = new Queue<int>(count);
            bool[] queued = new bool[count];
            for (int index = 0; index < count; index++)
            {
                if (elevation[index] == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                ValidateProfile(profiles[index]);
                queue.Enqueue(index);
                queued[index] = true;
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                queued[index] = false;
                int x = index % width;
                int y = index / width;
                bool currentChanged = false;

                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    int neighborY = y + offsetY;
                    if (neighborY < 0 || neighborY >= height)
                    {
                        continue;
                    }

                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        int neighborX = x + offsetX;
                        if (neighborX < 0 || neighborX >= width)
                        {
                            continue;
                        }

                        int neighbor = neighborY * width + neighborX;
                        if (elevation[neighbor] == TerrainElevationEncoding.NoData)
                        {
                            continue;
                        }

                        bool currentWater = IsWaterProfile(profiles[index]);
                        bool neighborWater = IsWaterProfile(profiles[neighbor]);
                        if (currentWater != neighborWater)
                        {
                            currentChanged |= TryLowerMagnitude(
                                elevation,
                                profiles,
                                index,
                                TerrainSpatialScale.GeneratedMaximumCoastalMagnitudeMetres);
                            if (TryLowerMagnitude(
                                elevation,
                                profiles,
                                neighbor,
                                TerrainSpatialScale.GeneratedMaximumCoastalMagnitudeMetres))
                            {
                                Enqueue(queue, queued, neighbor);
                            }

                            continue;
                        }

                        int riseLimit = offsetX != 0 && offsetY != 0
                            ? TerrainSpatialScale.GeneratedMaximumDiagonalRiseMetres
                            : TerrainSpatialScale.GeneratedMaximumCardinalRiseMetres;
                        int currentMagnitude = GetMagnitude(
                            elevation[index],
                            currentWater);
                        int neighborMagnitude = GetMagnitude(
                            elevation[neighbor],
                            neighborWater);
                        if (currentMagnitude > neighborMagnitude + riseLimit)
                        {
                            currentChanged |= TryLowerMagnitude(
                                elevation,
                                profiles,
                                index,
                                neighborMagnitude + riseLimit);
                            currentMagnitude = GetMagnitude(
                                elevation[index],
                                currentWater);
                        }

                        if (neighborMagnitude > currentMagnitude + riseLimit &&
                            TryLowerMagnitude(
                                elevation,
                                profiles,
                                neighbor,
                                currentMagnitude + riseLimit))
                        {
                            Enqueue(queue, queued, neighbor);
                        }
                    }
                }

                if (currentChanged)
                {
                    Enqueue(queue, queued, index);
                }
            }
        }

        private static void Enqueue(
            Queue<int> queue,
            bool[] queued,
            int index)
        {
            if (!queued[index])
            {
                queue.Enqueue(index);
                queued[index] = true;
            }
        }

        private static bool TryLowerMagnitude(
            short[] elevation,
            TerrainEarthElevationProfile[] profiles,
            int index,
            int maximumMagnitude)
        {
            TerrainEarthElevationProfile profile = profiles[index];
            bool water = IsWaterProfile(profile);
            int current = GetMagnitude(elevation[index], water);
            int next = Math.Max(GetMinimumMagnitude(profile), maximumMagnitude);
            if (next >= current)
            {
                return false;
            }

            elevation[index] = (short)(water ? -next : next);
            return true;
        }

        private static int GetMagnitude(short elevation, bool water)
        {
            return water ? -(int)elevation : elevation;
        }

        private static bool IsWaterProfile(TerrainEarthElevationProfile profile)
        {
            return profile == TerrainEarthElevationProfile.ShallowWater ||
                   profile == TerrainEarthElevationProfile.Shelf ||
                   profile == TerrainEarthElevationProfile.DeepOcean;
        }

        private static int GetMinimumMagnitude(
            TerrainEarthElevationProfile profile)
        {
            switch (profile)
            {
                case TerrainEarthElevationProfile.Shelf:
                    return 6;
                case TerrainEarthElevationProfile.DeepOcean:
                    return 150;
                default:
                    return 0;
            }
        }

        private static void ValidateProfile(TerrainEarthElevationProfile profile)
        {
            if ((int)profile < 0 || (int)profile >= ProfileCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    "Unknown generated elevation profile.");
            }
        }

        private static TerrainEarthElevationProfile GetProfile(
            WorldTile tile,
            TerrainLandform landform)
        {
            string id = tile?.main_type?.id ?? tile?.Type?.id ?? string.Empty;
            switch (id)
            {
                case "shallow_waters":
                case "pit_shallow_waters":
                    return TerrainEarthElevationProfile.ShallowWater;
                case "close_ocean":
                case "pit_close_ocean":
                    return TerrainEarthElevationProfile.Shelf;
                case "deep_ocean":
                case "pit_deep_ocean":
                    return TerrainEarthElevationProfile.DeepOcean;
            }

            switch (landform)
            {
                case TerrainLandform.Upland:
                    return TerrainEarthElevationProfile.Upland;
                case TerrainLandform.Hill:
                case TerrainLandform.Cliff:
                    return TerrainEarthElevationProfile.Hill;
                case TerrainLandform.Mountain:
                case TerrainLandform.Summit:
                    return TerrainEarthElevationProfile.Mountain;
                case TerrainLandform.Depression:
                    return TerrainEarthElevationProfile.DeepOcean;
                default:
                    return TerrainEarthElevationProfile.Lowland;
            }
        }

        private static int ClampRawHeight(short value)
        {
            return Math.Max(
                TerrainElevationEncoding.WorldBoxMinimum,
                Math.Min(TerrainElevationEncoding.WorldBoxMaximum, (int)value));
        }

        private static int GetRankedRawHeight(
            short value,
            TerrainLandform landform)
        {
            int rawHeight = ClampRawHeight(value);
            if (landform == TerrainLandform.Summit)
            {
                return TerrainElevationEncoding.WorldBoxMaximum;
            }

            if (landform == TerrainLandform.Mountain)
            {
                return Math.Min(
                    TerrainElevationEncoding.WorldBoxMaximum - 1,
                    rawHeight);
            }

            return rawHeight;
        }

        private static double Interpolate(
            double q,
            double q0,
            double value0,
            double q1,
            double value1)
        {
            if (q1 <= q0)
            {
                return value1;
            }

            double ratio = (q - q0) / (q1 - q0);
            return value0 + ratio * (value1 - value0);
        }

        private static double Interpolate(
            double q,
            double q0,
            double value0,
            double q1,
            double value1,
            double q2,
            double value2,
            double q3,
            double value3)
        {
            if (q <= q1)
            {
                return Interpolate(q, q0, value0, q1, value1);
            }

            if (q <= q2)
            {
                return Interpolate(q, q1, value1, q2, value2);
            }

            return Interpolate(q, q2, value2, q3, value3);
        }
    }
}
