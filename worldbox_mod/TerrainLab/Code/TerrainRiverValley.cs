using System;
using System.Collections.Generic;

namespace TerrainLab
{
    public enum TerrainHydroFeature : byte
    {
        None = 0,
        River = 1,
        Waterbody = 2
    }

    public sealed class TerrainRiverEvolution
    {
        internal TerrainRiverEvolution(
            bool dynamicChanged,
            int[] sandIndices,
            int[] clayIndices,
            int[] incisionIndices,
            short[] incisionElevations)
        {
            DynamicChanged = dynamicChanged;
            SandIndices = sandIndices ?? Array.Empty<int>();
            ClayIndices = clayIndices ?? Array.Empty<int>();
            IncisionIndices = incisionIndices ?? Array.Empty<int>();
            IncisionElevations = incisionElevations ?? Array.Empty<short>();
        }

        public bool DynamicChanged { get; }

        public int[] SandIndices { get; }

        public int[] ClayIndices { get; }

        public int[] IncisionIndices { get; }

        public short[] IncisionElevations { get; }

        public bool HasChanges => DynamicChanged || SandIndices.Length > 0 ||
                                  ClayIndices.Length > 0 ||
                                  IncisionIndices.Length > 0;
    }

    public static class TerrainRiverValleyModel
    {
        public const byte NoDirection = byte.MaxValue;
        public const int MaximumEncodedValue = byte.MaxValue - 1;
        public const int MaximumLocalIncisionDepth = 24;

        private const double MaximumSlopeRadians = Math.PI / 2.0;
        private const double FullTurn = Math.PI * 2.0;
        private const int SoilToSandMoisture = 156;
        private const int SoilToSandErodibility = 198;
        private const int SandToClayMoisture = 212;
        private const int IncisionPowerThreshold = 24;

        public static byte[] CreateNoDataLayer(int cellCount)
        {
            if (cellCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cellCount));
            }

            byte[] result = new byte[cellCount];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = NoDirection;
            }

            return result;
        }

        public static TerrainHydroFeature NormalizeFeature(byte value)
        {
            return value == (byte)TerrainHydroFeature.River
                ? TerrainHydroFeature.River
                : value == (byte)TerrainHydroFeature.Waterbody
                    ? TerrainHydroFeature.Waterbody
                    : TerrainHydroFeature.None;
        }

        public static byte GetBaseErodibility(byte material, byte landform)
        {
            int value;
            switch ((TerrainMaterial)material)
            {
                case TerrainMaterial.Clay:
                    value = 12;
                    break;
                case TerrainMaterial.Rock:
                    value = 4;
                    break;
                case TerrainMaterial.Ice:
                    value = 3;
                    break;
                case TerrainMaterial.Lava:
                case TerrainMaterial.Artificial:
                    value = 2;
                    break;
                case TerrainMaterial.Sand:
                    value = 148;
                    break;
                case TerrainMaterial.Organic:
                    value = 138;
                    break;
                case TerrainMaterial.Soil:
                    value = 166;
                    break;
                default:
                    value = 96;
                    break;
            }

            TerrainLandform form = (TerrainLandform)landform;
            if (form == TerrainLandform.Mountain || form == TerrainLandform.Summit ||
                form == TerrainLandform.Cliff)
            {
                value = Math.Min(value, 8);
            }

            return (byte)Math.Max(1, Math.Min(MaximumEncodedValue, value));
        }

        public static byte GetFlowResistance(
            byte material,
            byte hydroFeature,
            byte moisture)
        {
            int resistance;
            switch ((TerrainMaterial)material)
            {
                case TerrainMaterial.Clay:
                    resistance = 24;
                    break;
                case TerrainMaterial.Sand:
                    resistance = 72;
                    break;
                case TerrainMaterial.Soil:
                    resistance = 150;
                    break;
                case TerrainMaterial.Organic:
                    resistance = 214;
                    break;
                case TerrainMaterial.Rock:
                    resistance = 190;
                    break;
                case TerrainMaterial.Ice:
                    resistance = 82;
                    break;
                case TerrainMaterial.Lava:
                case TerrainMaterial.Artificial:
                    resistance = 230;
                    break;
                default:
                    resistance = 128;
                    break;
            }

            TerrainHydroFeature feature = NormalizeFeature(hydroFeature);
            if (feature == TerrainHydroFeature.River)
            {
                resistance -= 64;
            }
            else if (feature == TerrainHydroFeature.Waterbody)
            {
                resistance -= 42;
            }

            TerrainMaterial type = (TerrainMaterial)material;
            if (type == TerrainMaterial.Soil || type == TerrainMaterial.Organic ||
                type == TerrainMaterial.Sand)
            {
                resistance -= moisture / 6;
            }

            return (byte)Math.Max(8, Math.Min(byte.MaxValue, resistance));
        }

        public static double DecodeSlopeRadians(byte value)
        {
            return value == NoDirection
                ? double.NaN
                : value / (double)MaximumEncodedValue * MaximumSlopeRadians;
        }

        public static double DecodeAspectRadians(byte value)
        {
            return value == NoDirection
                ? double.NaN
                : value / (double)MaximumEncodedValue * FullTurn;
        }

        public static void CalculateLocalTerrain(
            int width,
            int height,
            short[] elevation,
            int index,
            out byte slope,
            out byte aspect,
            out int convergence)
        {
            int count = checked(width * height);
            if (width <= 0 || height <= 0 || elevation == null ||
                elevation.Length != count || index < 0 || index >= count)
            {
                throw new ArgumentException("Local terrain window does not match the DEM.");
            }

            short center = elevation[index];
            if (center == TerrainElevationEncoding.NoData)
            {
                slope = NoDirection;
                aspect = NoDirection;
                convergence = 0;
                return;
            }

            int x = index % width;
            int y = index / width;
            double northWest = Sample(elevation, width, height, x - 1, y + 1, center);
            double north = Sample(elevation, width, height, x, y + 1, center);
            double northEast = Sample(elevation, width, height, x + 1, y + 1, center);
            double west = Sample(elevation, width, height, x - 1, y, center);
            double east = Sample(elevation, width, height, x + 1, y, center);
            double southWest = Sample(elevation, width, height, x - 1, y - 1, center);
            double south = Sample(elevation, width, height, x, y - 1, center);
            double southEast = Sample(elevation, width, height, x + 1, y - 1, center);

            double dzdx =
                (northEast + 2.0 * east + southEast -
                 northWest - 2.0 * west - southWest) / 8.0;
            double dzdy =
                (northWest + 2.0 * north + northEast -
                 southWest - 2.0 * south - southEast) / 8.0;
            double gradient = Math.Sqrt(dzdx * dzdx + dzdy * dzdy);
            double slopeRadians = Math.Atan(gradient);
            slope = (byte)Math.Max(
                0,
                Math.Min(
                    MaximumEncodedValue,
                    Math.Round(
                        slopeRadians / MaximumSlopeRadians *
                        MaximumEncodedValue)));

            if (gradient < 1e-9)
            {
                aspect = NoDirection;
            }
            else
            {
                double aspectRadians = Math.Atan2(-dzdx, -dzdy);
                if (aspectRadians < 0.0)
                {
                    aspectRadians += FullTurn;
                }

                aspect = (byte)Math.Max(
                    0,
                    Math.Min(
                        MaximumEncodedValue,
                        Math.Round(aspectRadians / FullTurn * MaximumEncodedValue)));
            }

            convergence = (int)Math.Round(
                (north + east + south + west) / 4.0 - center);
        }

        public static TerrainHydroFeature ActivateCell(
            TerrainWorldState state,
            TerrainWaterState water,
            int index,
            int fillDepth = 0)
        {
            if (state == null || water == null || index < 0 || index >= state.CellCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (water.HydroFeature == null || water.HydroFeature.Length != state.CellCount ||
                water.Moisture == null || water.Moisture.Length != state.CellCount ||
                water.Erodibility == null || water.Erodibility.Length != state.CellCount ||
                water.LocalSlope == null || water.LocalSlope.Length != state.CellCount ||
                water.LocalAspect == null || water.LocalAspect.Length != state.CellCount)
            {
                throw new InvalidOperationException(
                    "River-valley layers do not match the project grid.");
            }
            CalculateLocalTerrain(
                state.Width,
                state.Height,
                state.Elevation,
                index,
                out byte slope,
                out byte aspect,
                out int convergence);
            TerrainHydroFeature feature = NormalizeFeature(water.HydroFeature[index]);
            if (feature == TerrainHydroFeature.None)
            {
                bool impounded = fillDepth > 0 && slope <= 48 ||
                                  slope <= 12 ||
                                  slope <= 28 && convergence > 0;
                feature = impounded
                    ? TerrainHydroFeature.Waterbody
                    : TerrainHydroFeature.River;
                water.HydroFeature[index] = (byte)feature;
            }

            water.LocalSlope[index] = slope;
            water.LocalAspect[index] = aspect;
            water.Moisture[index] = (byte)Math.Max(
                (int)water.Moisture[index],
                104);
            if (water.Erodibility[index] == 0)
            {
                water.Erodibility[index] = GetBaseErodibility(
                    state.Material[index],
                    state.Landform[index]);
            }

            water.IsDirty = true;
            return feature;
        }

        public static void ClearCell(TerrainWaterState water, int index)
        {
            if (water == null || water.HydroFeature == null || index < 0 ||
                index >= water.HydroFeature.Length)
            {
                return;
            }

            water.HydroFeature[index] = (byte)TerrainHydroFeature.None;
            water.Moisture[index] = 0;
            water.Erodibility[index] = 0;
            water.LocalSlope[index] = NoDirection;
            water.LocalAspect[index] = NoDirection;
            water.IsDirty = true;
        }

        public static TerrainRiverEvolution Step(
            TerrainWorldState state,
            TerrainWaterState water,
            int maximumMorphologyChanges)
        {
            if (state == null || water == null)
            {
                return new TerrainRiverEvolution(
                    false,
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<short>());
            }

            water.ValidateAndRecount(state.CellCount);
            int changeLimit = Math.Max(1, Math.Min(8192, maximumMorphologyChanges));
            List<int> sand = new List<int>();
            List<int> clay = new List<int>();
            List<int> incision = new List<int>();
            List<short> incisionElevation = new List<short>();
            bool dynamicChanged = false;

            for (int index = 0; index < state.CellCount; index++)
            {
                bool wet = water.ManagedMask[index] != 0 &&
                           water.WaterStorage[index] != 0;
                TerrainHydroFeature feature = NormalizeFeature(
                    water.HydroFeature[index]);
                if (!wet && feature == TerrainHydroFeature.None &&
                    water.Moisture[index] == 0)
                {
                    continue;
                }

                CalculateLocalTerrain(
                    state.Width,
                    state.Height,
                    state.Elevation,
                    index,
                    out byte slope,
                    out byte aspect,
                    out int convergence);
                if (slope == NoDirection)
                {
                    continue;
                }

                if (water.LocalSlope[index] != slope ||
                    water.LocalAspect[index] != aspect)
                {
                    water.LocalSlope[index] = slope;
                    water.LocalAspect[index] = aspect;
                    dynamicChanged = true;
                }

                if (wet && feature == TerrainHydroFeature.None)
                {
                    feature = ActivateCell(state, water, index);
                    dynamicChanged = true;
                }

                byte material = state.Material[index];
                int oldMoisture = water.Moisture[index];
                int newMoisture;
                if (wet)
                {
                    int retention = GetWaterRetention(material);
                    int gain = 3 + retention / 32 +
                               water.WaterStorage[index] / 32 +
                               Math.Max(0, Math.Min(4, convergence / 8));
                    newMoisture = Math.Min(byte.MaxValue, oldMoisture + gain);
                }
                else
                {
                    int loss = GetDryingRate(material, feature);
                    newMoisture = Math.Max(0, oldMoisture - loss);
                }

                if (newMoisture != oldMoisture)
                {
                    water.Moisture[index] = (byte)newMoisture;
                    dynamicChanged = true;
                }

                int baseErodibility = GetBaseErodibility(
                    material,
                    state.Landform[index]);
                int oldErodibility = water.Erodibility[index];
                if (oldErodibility == 0)
                {
                    oldErodibility = baseErodibility;
                }

                int newErodibility = oldErodibility;
                if (wet && ((TerrainMaterial)material == TerrainMaterial.Soil ||
                            (TerrainMaterial)material == TerrainMaterial.Organic))
                {
                    newErodibility = Math.Min(
                        MaximumEncodedValue,
                        oldErodibility + 2 + newMoisture / 80);
                }
                else if (oldErodibility != baseErodibility)
                {
                    newErodibility += oldErodibility < baseErodibility ? 1 : -1;
                }

                if (newErodibility != water.Erodibility[index])
                {
                    water.Erodibility[index] = (byte)newErodibility;
                    dynamicChanged = true;
                }

                if (wet && sand.Count + clay.Count < changeLimit &&
                    feature == TerrainHydroFeature.River &&
                    ((TerrainMaterial)material == TerrainMaterial.Soil ||
                     (TerrainMaterial)material == TerrainMaterial.Organic) &&
                    newMoisture >= SoilToSandMoisture &&
                    newErodibility >= SoilToSandErodibility)
                {
                    sand.Add(index);
                    water.Erodibility[index] = GetBaseErodibility(
                        (byte)TerrainMaterial.Sand,
                        (byte)TerrainLandform.Channel);
                    material = (byte)TerrainMaterial.Sand;
                    dynamicChanged = true;
                }
                else if (wet && sand.Count + clay.Count < changeLimit &&
                         (TerrainMaterial)material == TerrainMaterial.Sand &&
                         newMoisture >= SandToClayMoisture &&
                         (feature == TerrainHydroFeature.Waterbody ||
                          slope <= 52 && convergence > 0))
                {
                    clay.Add(index);
                    water.Erodibility[index] = GetBaseErodibility(
                        (byte)TerrainMaterial.Clay,
                        (byte)TerrainLandform.Channel);
                    material = (byte)TerrainMaterial.Clay;
                    dynamicChanged = true;
                }

                if (!wet || feature != TerrainHydroFeature.River ||
                    incision.Count >= changeLimit ||
                    IsResistantMaterial((TerrainMaterial)material))
                {
                    continue;
                }

                int resistance = GetFlowResistance(
                    material,
                    water.HydroFeature[index],
                    water.Moisture[index]);
                int streamPower =
                    (water.Moisture[index] + water.WaterStorage[index] * 4) *
                    (slope + 8) * water.Erodibility[index] /
                    Math.Max(1, (resistance + 32) * byte.MaxValue);
                if (streamPower < IncisionPowerThreshold)
                {
                    continue;
                }

                short before = state.Elevation[index];
                int minimumNeighbor = GetMinimumNeighborElevation(
                    state.Width,
                    state.Height,
                    state.Elevation,
                    index,
                    before);
                int localFloor = Math.Max(
                    TerrainElevationEncoding.Minimum,
                    minimumNeighbor - MaximumLocalIncisionDepth);
                int amount = 1 + Math.Min(2, (streamPower - IncisionPowerThreshold) / 96);
                short after = (short)Math.Max(localFloor, before - amount);
                if (after < before)
                {
                    incision.Add(index);
                    incisionElevation.Add(after);
                }
            }

            water.IsDirty |= dynamicChanged;
            return new TerrainRiverEvolution(
                dynamicChanged,
                sand.ToArray(),
                clay.ToArray(),
                incision.ToArray(),
                incisionElevation.ToArray());
        }

        private static int GetWaterRetention(byte material)
        {
            switch ((TerrainMaterial)material)
            {
                case TerrainMaterial.Clay:
                    return 224;
                case TerrainMaterial.Organic:
                    return 194;
                case TerrainMaterial.Soil:
                    return 160;
                case TerrainMaterial.Sand:
                    return 74;
                case TerrainMaterial.Rock:
                    return 24;
                case TerrainMaterial.Ice:
                    return 10;
                default:
                    return 40;
            }
        }

        private static int GetDryingRate(
            byte material,
            TerrainHydroFeature feature)
        {
            int loss;
            switch ((TerrainMaterial)material)
            {
                case TerrainMaterial.Clay:
                    loss = 1;
                    break;
                case TerrainMaterial.Organic:
                case TerrainMaterial.Soil:
                    loss = 2;
                    break;
                case TerrainMaterial.Sand:
                    loss = 4;
                    break;
                default:
                    loss = 5;
                    break;
            }

            return feature == TerrainHydroFeature.Waterbody
                ? Math.Max(1, loss - 1)
                : loss;
        }

        private static bool IsResistantMaterial(TerrainMaterial material)
        {
            return material == TerrainMaterial.Clay ||
                   material == TerrainMaterial.Rock ||
                   material == TerrainMaterial.Ice ||
                   material == TerrainMaterial.Lava ||
                   material == TerrainMaterial.Artificial;
        }

        private static double Sample(
            short[] elevation,
            int width,
            int height,
            int x,
            int y,
            short fallback)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                return fallback;
            }

            short value = elevation[y * width + x];
            return value == TerrainElevationEncoding.NoData ? fallback : value;
        }

        private static int GetMinimumNeighborElevation(
            int width,
            int height,
            short[] elevation,
            int index,
            int fallback)
        {
            int x = index % width;
            int y = index / width;
            int minimum = fallback;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int neighborY = y + offsetY;
                if (neighborY < 0 || neighborY >= height)
                {
                    continue;
                }

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int neighborX = x + offsetX;
                    if ((offsetX == 0 && offsetY == 0) ||
                        neighborX < 0 || neighborX >= width)
                    {
                        continue;
                    }

                    short value = elevation[neighborY * width + neighborX];
                    if (value != TerrainElevationEncoding.NoData)
                    {
                        minimum = Math.Min(minimum, value);
                    }
                }
            }

            return minimum;
        }
    }
}
