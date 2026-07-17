using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TerrainLab
{
    public enum TerrainWaterDepthClass : byte
    {
        Shallow = 0,
        Shelf = 1,
        Deep = 2
    }

    public static class TerrainWaterDepthModel
    {
        public const int ShallowMinimumElevation = -5;
        public const int ShelfMinimumElevation = -150;
        public const byte ShallowStorageUnits = 5;
        public const byte ShelfStorageUnits = 150;
        public const byte MaximumStoredDepth = byte.MaxValue;

        public static int GetDepthMetres(int bedElevation, int waterSurfaceElevation)
        {
            return Math.Max(0, waterSurfaceElevation - bedElevation);
        }

        public static bool TryClassifyElevation(
            int bedElevation,
            int seaLevel,
            out TerrainWaterDepthClass depthClass)
        {
            int relativeElevation = bedElevation - seaLevel;
            if (relativeElevation > 0)
            {
                depthClass = default(TerrainWaterDepthClass);
                return false;
            }

            if (relativeElevation >= ShallowMinimumElevation)
            {
                depthClass = TerrainWaterDepthClass.Shallow;
            }
            else if (relativeElevation > ShelfMinimumElevation)
            {
                depthClass = TerrainWaterDepthClass.Shelf;
            }
            else
            {
                depthClass = TerrainWaterDepthClass.Deep;
            }

            return true;
        }

        public static TerrainWaterDepthClass ClassifyElevation(
            int bedElevation,
            int seaLevel)
        {
            if (!TryClassifyElevation(bedElevation, seaLevel, out TerrainWaterDepthClass result))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bedElevation),
                    "Water cannot be classified above sea level.");
            }

            return result;
        }

        public static byte GetStorage(int depthMetres)
        {
            return (byte)Math.Max(
                1,
                Math.Min(MaximumStoredDepth, depthMetres));
        }

        public static byte GetStorage(TerrainWaterDepthClass depthClass)
        {
            switch (depthClass)
            {
                case TerrainWaterDepthClass.Deep:
                    return MaximumStoredDepth;
                case TerrainWaterDepthClass.Shelf:
                    return ShelfStorageUnits;
                default:
                    return ShallowStorageUnits;
            }
        }
    }

    public enum TerrainWaterRoutingAlgorithm : byte
    {
        D8 = 0,
        DInfinity = 1,
        MultipleFlowDirection = 2
    }

    public static class TerrainWaterRoutingAlgorithms
    {
        public static TerrainWaterRoutingAlgorithm Normalize(
            TerrainWaterRoutingAlgorithm algorithm)
        {
            return algorithm == TerrainWaterRoutingAlgorithm.DInfinity ||
                   algorithm == TerrainWaterRoutingAlgorithm.MultipleFlowDirection
                ? algorithm
                : TerrainWaterRoutingAlgorithm.D8;
        }

        public static string ToStorageId(TerrainWaterRoutingAlgorithm algorithm)
        {
            switch (Normalize(algorithm))
            {
                case TerrainWaterRoutingAlgorithm.DInfinity:
                    return "dinf";
                case TerrainWaterRoutingAlgorithm.MultipleFlowDirection:
                    return "mfd";
                default:
                    return "d8";
            }
        }

        public static TerrainWaterRoutingAlgorithm ParseStorageId(string value)
        {
            if (string.Equals(value, "dinf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "d-infinity", StringComparison.OrdinalIgnoreCase))
            {
                return TerrainWaterRoutingAlgorithm.DInfinity;
            }

            if (string.Equals(value, "mfd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    value,
                    "multiple-flow-direction",
                    StringComparison.OrdinalIgnoreCase))
            {
                return TerrainWaterRoutingAlgorithm.MultipleFlowDirection;
            }

            return TerrainWaterRoutingAlgorithm.D8;
        }
    }

    public sealed class TerrainWaterParameters
    {
        public const int HardMaximumFloodPercent = 100;
        public const int MinimumBankErosionRadius = 1;
        public const int MaximumBankErosionRadius = 2;
        public const int MinimumOrphanedChannelDrain = 1;
        public const int MaximumOrphanedChannelDrain = 64;

        [JsonProperty("maximum_flood_percent")]
        public int MaximumFloodPercent { get; set; } = 50;

        [JsonProperty("initial_source_volume")]
        public int InitialSourceVolume { get; set; } = 32;

        [JsonProperty("geyser_pulse_volume")]
        public int GeyserPulseVolume { get; set; } = 2;

        [JsonProperty("cells_per_tick")]
        public int CellsPerTick { get; set; } = 48;

        [JsonProperty("evaporation_per_climate_step")]
        public int EvaporationPerClimateStep { get; set; } = 1;

        [JsonProperty("bank_erosion_radius")]
        public int BankErosionRadius { get; set; } = MaximumBankErosionRadius;

        [JsonProperty("orphaned_channel_drain_per_climate_step")]
        public int OrphanedChannelDrainPerClimateStep { get; set; } = 8;

        [JsonIgnore]
        public TerrainWaterRoutingAlgorithm RoutingAlgorithm { get; set; } =
            TerrainWaterRoutingAlgorithm.D8;

        [JsonProperty("routing_algorithm")]
        public string RoutingAlgorithmId
        {
            get => TerrainWaterRoutingAlgorithms.ToStorageId(RoutingAlgorithm);
            set => RoutingAlgorithm =
                TerrainWaterRoutingAlgorithms.ParseStorageId(value);
        }

        public TerrainWaterParameters Normalize()
        {
            return new TerrainWaterParameters
            {
                MaximumFloodPercent = Math.Max(
                    1,
                    Math.Min(HardMaximumFloodPercent, MaximumFloodPercent)),
                InitialSourceVolume = Math.Max(1, Math.Min(4096, InitialSourceVolume)),
                GeyserPulseVolume = Math.Max(1, Math.Min(1024, GeyserPulseVolume)),
                CellsPerTick = Math.Max(1, Math.Min(512, CellsPerTick)),
                EvaporationPerClimateStep = Math.Max(
                    0,
                    Math.Min(16, EvaporationPerClimateStep)),
                BankErosionRadius = Math.Max(
                    MinimumBankErosionRadius,
                    Math.Min(MaximumBankErosionRadius, BankErosionRadius)),
                OrphanedChannelDrainPerClimateStep = Math.Max(
                    MinimumOrphanedChannelDrain,
                    Math.Min(
                        MaximumOrphanedChannelDrain,
                        OrphanedChannelDrainPerClimateStep)),
                RoutingAlgorithm = TerrainWaterRoutingAlgorithms.Normalize(
                    RoutingAlgorithm)
            };
        }
    }

    public sealed class TerrainWaterState
    {
        public bool Enabled { get; internal set; }

        public TerrainWaterParameters Parameters { get; internal set; } =
            new TerrainWaterParameters();

        public byte[] ManagedMask { get; internal set; }

        public byte[] WaterStorage { get; internal set; }

        public byte[] HydroFeature { get; internal set; }

        public byte[] Moisture { get; internal set; }

        public byte[] Erodibility { get; internal set; }

        public byte[] LocalSlope { get; internal set; }

        public byte[] LocalAspect { get; internal set; }

        public byte[] RestoreSurfaceCodes { get; internal set; }

        public List<TerrainSurfaceStamp> RestoreSurfacePalette { get; internal set; } =
            new List<TerrainSurfaceStamp>();

        public int ManagedCellCount { get; internal set; }

        public long TotalInjectedVolume { get; internal set; }

        public long TotalConsumedVolume { get; internal set; }

        public long GeyserPulseCount { get; internal set; }

        public bool IsDirty { get; internal set; }

        public static TerrainWaterState Create(int cellCount)
        {
            if (cellCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cellCount));
            }

            return new TerrainWaterState
            {
                ManagedMask = new byte[cellCount],
                WaterStorage = new byte[cellCount],
                HydroFeature = new byte[cellCount],
                Moisture = new byte[cellCount],
                Erodibility = new byte[cellCount],
                LocalSlope = TerrainRiverValleyModel.CreateNoDataLayer(cellCount),
                LocalAspect = TerrainRiverValleyModel.CreateNoDataLayer(cellCount),
                RestoreSurfaceCodes = new byte[cellCount]
            };
        }

        internal void ValidateAndRecount(int cellCount)
        {
            if (ManagedMask == null || ManagedMask.Length != cellCount)
            {
                ManagedMask = new byte[cellCount];
            }

            if (WaterStorage == null || WaterStorage.Length != cellCount)
            {
                WaterStorage = new byte[cellCount];
            }

            if (HydroFeature == null || HydroFeature.Length != cellCount)
            {
                HydroFeature = new byte[cellCount];
            }

            if (Moisture == null || Moisture.Length != cellCount)
            {
                Moisture = new byte[cellCount];
            }

            if (Erodibility == null || Erodibility.Length != cellCount)
            {
                Erodibility = new byte[cellCount];
            }

            if (LocalSlope == null || LocalSlope.Length != cellCount)
            {
                LocalSlope = TerrainRiverValleyModel.CreateNoDataLayer(cellCount);
            }

            if (LocalAspect == null || LocalAspect.Length != cellCount)
            {
                LocalAspect = TerrainRiverValleyModel.CreateNoDataLayer(cellCount);
            }

            if (RestoreSurfaceCodes == null || RestoreSurfaceCodes.Length != cellCount)
            {
                RestoreSurfaceCodes = new byte[cellCount];
            }

            RestoreSurfacePalette = RestoreSurfacePalette ??
                new List<TerrainSurfaceStamp>();
            if (RestoreSurfacePalette.Count > byte.MaxValue)
            {
                RestoreSurfacePalette.RemoveRange(
                    byte.MaxValue,
                    RestoreSurfacePalette.Count - byte.MaxValue);
            }

            int count = 0;
            for (int index = 0; index < ManagedMask.Length; index++)
            {
                HydroFeature[index] = (byte)TerrainRiverValleyModel.NormalizeFeature(
                    HydroFeature[index]);
                if (ManagedMask[index] == 0)
                {
                    WaterStorage[index] = 0;
                    RestoreSurfaceCodes[index] = 0;
                    continue;
                }

                ManagedMask[index] = 1;
                if (WaterStorage[index] == 0)
                {
                    WaterStorage[index] = TerrainWaterDepthModel.GetStorage(
                        TerrainWaterDepthClass.Shallow);
                }

                if (RestoreSurfaceCodes[index] > RestoreSurfacePalette.Count)
                {
                    RestoreSurfaceCodes[index] = 0;
                }

                count++;
            }

            ManagedCellCount = count;
            Parameters = (Parameters ?? new TerrainWaterParameters()).Normalize();
        }

        internal void CaptureRestoreSurface(int index, TerrainSurfaceStamp stamp)
        {
            if (index < 0 || index >= RestoreSurfaceCodes.Length ||
                 RestoreSurfaceCodes[index] != 0 ||
                string.IsNullOrWhiteSpace(stamp.MainTypeId))
            {
                return;
            }

            SetRestoreSurface(index, stamp);
        }

        internal void SetRestoreSurface(int index, TerrainSurfaceStamp stamp)
        {
            if (index < 0 || index >= RestoreSurfaceCodes.Length ||
                string.IsNullOrWhiteSpace(stamp.MainTypeId))
            {
                return;
            }

            int paletteIndex = RestoreSurfacePalette.IndexOf(stamp);
            if (paletteIndex < 0)
            {
                if (RestoreSurfacePalette.Count >= byte.MaxValue)
                {
                    return;
                }

                paletteIndex = RestoreSurfacePalette.Count;
                RestoreSurfacePalette.Add(stamp);
            }

            RestoreSurfaceCodes[index] = checked((byte)(paletteIndex + 1));
        }

        internal bool TryGetRestoreSurface(int index, out TerrainSurfaceStamp stamp)
        {
            stamp = default(TerrainSurfaceStamp);
            if (index < 0 || index >= RestoreSurfaceCodes.Length)
            {
                return false;
            }

            int paletteIndex = RestoreSurfaceCodes[index] - 1;
            if (paletteIndex < 0 || paletteIndex >= RestoreSurfacePalette.Count)
            {
                return false;
            }

            stamp = RestoreSurfacePalette[paletteIndex];
            return !string.IsNullOrWhiteSpace(stamp.MainTypeId);
        }

        internal void ClearManagedStorage(int index)
        {
            if (index < 0 || index >= ManagedMask.Length)
            {
                return;
            }

            WaterStorage[index] = 0;
            RestoreSurfaceCodes[index] = 0;
        }
    }

    public static class TerrainWaterBalance
    {
        public static int ApplyEvaporation(
            byte[] managedMask,
            byte[] waterStorage,
            int evaporation,
            ICollection<int> driedCells)
        {
            if (managedMask == null || waterStorage == null ||
                managedMask.Length != waterStorage.Length)
            {
                throw new ArgumentException("Water balance layers have different sizes.");
            }

            int loss = Math.Max(0, Math.Min(byte.MaxValue, evaporation));
            if (loss == 0)
            {
                return 0;
            }

            int dried = 0;
            for (int index = 0; index < managedMask.Length; index++)
            {
                if (managedMask[index] == 0)
                {
                    waterStorage[index] = 0;
                    continue;
                }

                int remaining = waterStorage[index] - loss;
                if (remaining > 0)
                {
                    waterStorage[index] = (byte)remaining;
                    continue;
                }

                waterStorage[index] = 0;
                driedCells?.Add(index);
                dried++;
            }

            return dried;
        }

        public static int ApplyTargetedLoss(
            byte[] managedMask,
            byte[] waterStorage,
            IEnumerable<int> indices,
            int loss,
            ICollection<int> driedCells)
        {
            if (managedMask == null || waterStorage == null ||
                managedMask.Length != waterStorage.Length)
            {
                throw new ArgumentException("Water balance layers have different sizes.");
            }

            if (indices == null)
            {
                return 0;
            }

            int normalizedLoss = Math.Max(0, Math.Min(byte.MaxValue, loss));
            if (normalizedLoss == 0)
            {
                return 0;
            }

            int changed = 0;
            HashSet<int> visited = new HashSet<int>();
            foreach (int index in indices)
            {
                if (index < 0 || index >= managedMask.Length ||
                    !visited.Add(index) || managedMask[index] == 0 ||
                    waterStorage[index] == 0)
                {
                    continue;
                }

                int remaining = waterStorage[index] - normalizedLoss;
                changed++;
                if (remaining > 0)
                {
                    waterStorage[index] = (byte)remaining;
                    continue;
                }

                waterStorage[index] = 0;
                driedCells?.Add(index);
            }

            return changed;
        }
    }

    public readonly struct TerrainWaterReceiver
    {
        public TerrainWaterReceiver(int index, double weight)
        {
            Index = index;
            Weight = weight;
        }

        public int Index { get; }

        public double Weight { get; }
    }

    public sealed class TerrainWaterRouting
    {
        private static readonly int[] DirectionX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DirectionY = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private const double QuarterTurn = Math.PI / 4.0;
        private const double DiagonalDistance = 1.4142135623730951;
        private const double MfdExponent = 1.1;

        private TerrainWaterRouting(
            int width,
            int height,
            short[] elevation,
            short[] filledElevation,
            int[] receiver,
            int[] drainageRank,
            int validCellCount,
            int verticalUnit)
        {
            Width = width;
            Height = height;
            Elevation = elevation;
            FilledElevation = filledElevation;
            Receiver = receiver;
            DrainageRank = drainageRank;
            ValidCellCount = validCellCount;
            VerticalUnit = verticalUnit;
        }

        public int Width { get; }

        public int Height { get; }

        public int CellCount => checked(Width * Height);

        public short[] Elevation { get; }

        public short[] FilledElevation { get; }

        public int[] Receiver { get; }

        public int[] DrainageRank { get; }

        public int ValidCellCount { get; }

        public int VerticalUnit { get; }

        public static TerrainWaterRouting Build(
            int width,
            int height,
            short[] elevation)
        {
            int count = checked(width * height);
            if (width <= 0 || height <= 0 || elevation == null || elevation.Length != count)
            {
                throw new ArgumentException("Water routing DEM does not match the canvas.");
            }

            short[] filled = (short[])elevation.Clone();
            int[] receiver = Enumerable.Repeat(-1, count).ToArray();
            int[] drainageRank = Enumerable.Repeat(-1, count).ToArray();
            byte[] visited = new byte[count];
            CellMinHeap queue = new CellMinHeap(filled, Math.Min(count, 4096));
            int validCount = 0;
            int minimum = int.MaxValue;
            int maximum = int.MinValue;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    short value = elevation[index];
                    if (value == TerrainElevationEncoding.NoData)
                    {
                        continue;
                    }

                    validCount++;
                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                    if (!IsOutletCell(x, y, index, width, height, elevation))
                    {
                        continue;
                    }

                    visited[index] = 1;
                    queue.Push(index);
                }
            }

            if (validCount == 0 || queue.Count == 0)
            {
                throw new InvalidOperationException(
                    "Water routing requires at least one valid DEM outlet.");
            }

            int processed = 0;
            while (queue.Count > 0)
            {
                int current = queue.Pop();
                drainageRank[current] = processed++;
                int currentX = current % width;
                int currentY = current / width;
                short spillElevation = filled[current];

                for (int direction = 0; direction < DirectionX.Length; direction++)
                {
                    int neighborX = currentX + DirectionX[direction];
                    int neighborY = currentY + DirectionY[direction];
                    if (neighborX < 0 || neighborX >= width ||
                        neighborY < 0 || neighborY >= height)
                    {
                        continue;
                    }

                    int neighbor = neighborY * width + neighborX;
                    if (visited[neighbor] != 0 ||
                        elevation[neighbor] == TerrainElevationEncoding.NoData)
                    {
                        continue;
                    }

                    visited[neighbor] = 1;
                    if (filled[neighbor] < spillElevation)
                    {
                        filled[neighbor] = spillElevation;
                    }

                    receiver[neighbor] = current;
                    queue.Push(neighbor);
                }
            }

            if (processed != validCount)
            {
                throw new InvalidOperationException(
                    "Water routing did not visit every valid DEM cell.");
            }

            long range = (long)maximum - minimum;
            int verticalUnit = Math.Max(1, (int)Math.Min(int.MaxValue, (range + 127L) / 128L));
            return new TerrainWaterRouting(
                width,
                height,
                (short[])elevation.Clone(),
                filled,
                receiver,
                drainageRank,
                validCount,
                verticalUnit);
        }

        public int GetReceivers(
            int index,
            TerrainWaterRoutingAlgorithm algorithm,
            TerrainWaterReceiver[] output)
        {
            if (output == null || output.Length < DirectionX.Length)
            {
                throw new ArgumentException(
                    "Water receiver output must hold eight cells.",
                    nameof(output));
            }

            if (index < 0 || index >= CellCount ||
                Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return 0;
            }

            switch (TerrainWaterRoutingAlgorithms.Normalize(algorithm))
            {
                case TerrainWaterRoutingAlgorithm.DInfinity:
                    return GetDInfinityReceivers(index, output);
                case TerrainWaterRoutingAlgorithm.MultipleFlowDirection:
                    return GetMfdReceivers(index, output);
                default:
                    return GetD8Receiver(index, output);
            }
        }

        public int GetFillDepth(int index)
        {
            if (index < 0 || index >= CellCount ||
                Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return 0;
            }

            return Math.Max(0, (int)FilledElevation[index] - Elevation[index]);
        }

        internal void ForEachNeighbor(int index, Action<int> action)
        {
            int x = index % Width;
            int y = index / Width;
            for (int direction = 0; direction < DirectionX.Length; direction++)
            {
                int neighborX = x + DirectionX[direction];
                int neighborY = y + DirectionY[direction];
                if (neighborX >= 0 && neighborX < Width &&
                    neighborY >= 0 && neighborY < Height)
                {
                    action(neighborY * Width + neighborX);
                }
            }
        }

        internal bool IsBoundary(int index)
        {
            if (index < 0 || index >= CellCount)
            {
                return false;
            }

            int x = index % Width;
            int y = index / Width;
            return x == 0 || y == 0 || x == Width - 1 || y == Height - 1;
        }

        private int GetD8Receiver(int index, TerrainWaterReceiver[] output)
        {
            int receiver = Receiver[index];
            if (receiver < 0)
            {
                return 0;
            }

            output[0] = new TerrainWaterReceiver(receiver, 1.0);
            return 1;
        }

        private int GetDInfinityReceivers(
            int index,
            TerrainWaterReceiver[] output)
        {
            int x = index % Width;
            int y = index / Width;
            double center = FilledElevation[index];
            double bestSlope = 0.0;
            int bestCardinal = -1;
            int bestDiagonal = -1;
            double bestCardinalWeight = 0.0;
            double bestDiagonalWeight = 0.0;

            for (int cardinalDirection = 0;
                 cardinalDirection < DirectionX.Length;
                 cardinalDirection += 2)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    int diagonalDirection =
                        (cardinalDirection + side + DirectionX.Length) %
                        DirectionX.Length;
                    int cardinal = GetNeighborIndex(x, y, cardinalDirection);
                    int diagonal = GetNeighborIndex(x, y, diagonalDirection);
                    if (cardinal < 0 || diagonal < 0 ||
                        Elevation[cardinal] == TerrainElevationEncoding.NoData ||
                        Elevation[diagonal] == TerrainElevationEncoding.NoData)
                    {
                        continue;
                    }

                    double firstSlope = center - FilledElevation[cardinal];
                    double crossSlope =
                        FilledElevation[cardinal] - FilledElevation[diagonal];
                    double angle = Math.Atan2(crossSlope, firstSlope);
                    double slope;
                    if (angle <= 0.0)
                    {
                        angle = 0.0;
                        slope = firstSlope;
                    }
                    else if (angle >= QuarterTurn)
                    {
                        angle = QuarterTurn;
                        slope = (center - FilledElevation[diagonal]) /
                                DiagonalDistance;
                    }
                    else
                    {
                        slope = Math.Sqrt(
                            firstSlope * firstSlope + crossSlope * crossSlope);
                    }

                    if (slope <= bestSlope)
                    {
                        continue;
                    }

                    double diagonalWeight = angle / QuarterTurn;
                    double cardinalWeight = 1.0 - diagonalWeight;
                    bool cardinalValid = cardinalWeight > 0.0 &&
                        IsDrainageCandidate(index, cardinal);
                    bool diagonalValid = diagonalWeight > 0.0 &&
                        IsDrainageCandidate(index, diagonal);
                    if (!cardinalValid && !diagonalValid)
                    {
                        continue;
                    }

                    bestSlope = slope;
                    bestCardinal = cardinalValid ? cardinal : -1;
                    bestDiagonal = diagonalValid ? diagonal : -1;
                    bestCardinalWeight = cardinalValid ? cardinalWeight : 0.0;
                    bestDiagonalWeight = diagonalValid ? diagonalWeight : 0.0;
                }
            }

            double totalWeight = bestCardinalWeight + bestDiagonalWeight;
            if (bestSlope <= 0.0 || totalWeight <= 0.0)
            {
                return GetD8Receiver(index, output);
            }

            int count = 0;
            if (bestCardinal >= 0)
            {
                output[count++] = new TerrainWaterReceiver(
                    bestCardinal,
                    bestCardinalWeight / totalWeight);
            }

            if (bestDiagonal >= 0 && bestDiagonal != bestCardinal)
            {
                output[count++] = new TerrainWaterReceiver(
                    bestDiagonal,
                    bestDiagonalWeight / totalWeight);
            }

            return count > 0 ? count : GetD8Receiver(index, output);
        }

        private int GetMfdReceivers(
            int index,
            TerrainWaterReceiver[] output)
        {
            int x = index % Width;
            int y = index / Width;
            double center = FilledElevation[index];
            double totalWeight = 0.0;
            int count = 0;
            for (int direction = 0; direction < DirectionX.Length; direction++)
            {
                int neighbor = GetNeighborIndex(x, y, direction);
                if (neighbor < 0 || !IsDrainageCandidate(index, neighbor))
                {
                    continue;
                }

                double drop = center - FilledElevation[neighbor];
                if (drop <= 0.0)
                {
                    continue;
                }

                double distance = direction % 2 == 0 ? 1.0 : DiagonalDistance;
                double weight = Math.Pow(drop / distance, MfdExponent);
                if (weight <= 0.0 || double.IsNaN(weight))
                {
                    continue;
                }

                output[count++] = new TerrainWaterReceiver(neighbor, weight);
                totalWeight += weight;
            }

            if (count == 0 || totalWeight <= 0.0)
            {
                return GetD8Receiver(index, output);
            }

            for (int outputIndex = 0; outputIndex < count; outputIndex++)
            {
                TerrainWaterReceiver receiver = output[outputIndex];
                output[outputIndex] = new TerrainWaterReceiver(
                    receiver.Index,
                    receiver.Weight / totalWeight);
            }

            return count;
        }

        private bool IsDrainageCandidate(int source, int candidate)
        {
            return candidate >= 0 && candidate < CellCount &&
                   Elevation[candidate] != TerrainElevationEncoding.NoData &&
                   DrainageRank[candidate] >= 0 &&
                   DrainageRank[candidate] < DrainageRank[source] &&
                   FilledElevation[candidate] <= FilledElevation[source];
        }

        private int GetNeighborIndex(int x, int y, int direction)
        {
            int neighborX = x + DirectionX[direction];
            int neighborY = y + DirectionY[direction];
            return neighborX < 0 || neighborX >= Width ||
                   neighborY < 0 || neighborY >= Height
                ? -1
                : neighborY * Width + neighborX;
        }

        private static bool IsOutletCell(
            int x,
            int y,
            int index,
            int width,
            int height,
            short[] elevation)
        {
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
            {
                return true;
            }

            for (int direction = 0; direction < DirectionX.Length; direction++)
            {
                int neighbor = (y + DirectionY[direction]) * width +
                               x + DirectionX[direction];
                if (elevation[neighbor] == TerrainElevationEncoding.NoData)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class CellMinHeap
        {
            private readonly short[] _priority;
            private int[] _items;

            public CellMinHeap(short[] priority, int capacity)
            {
                _priority = priority;
                _items = new int[Math.Max(4, capacity)];
            }

            public int Count { get; private set; }

            public void Push(int value)
            {
                if (Count == _items.Length)
                {
                    Array.Resize(ref _items, checked(_items.Length * 2));
                }

                int position = Count++;
                while (position > 0)
                {
                    int parent = (position - 1) / 2;
                    if (Compare(_items[parent], value) <= 0)
                    {
                        break;
                    }

                    _items[position] = _items[parent];
                    position = parent;
                }

                _items[position] = value;
            }

            public int Pop()
            {
                int result = _items[0];
                int replacement = _items[--Count];
                if (Count == 0)
                {
                    return result;
                }

                int position = 0;
                while (true)
                {
                    int left = position * 2 + 1;
                    if (left >= Count)
                    {
                        break;
                    }

                    int right = left + 1;
                    int child = right < Count && Compare(_items[right], _items[left]) < 0
                        ? right
                        : left;
                    if (Compare(replacement, _items[child]) <= 0)
                    {
                        break;
                    }

                    _items[position] = _items[child];
                    position = child;
                }

                _items[position] = replacement;
                return result;
            }

            private int Compare(int left, int right)
            {
                int elevation = _priority[left].CompareTo(_priority[right]);
                return elevation != 0 ? elevation : left.CompareTo(right);
            }
        }
    }

    public readonly struct TerrainWaterCellChange
    {
        public TerrainWaterCellChange(
            int index,
            TerrainWaterDepthClass depthClass,
            int depthMetres,
            int cost,
            int sourceIndex)
        {
            Index = index;
            DepthClass = depthClass;
            DepthMetres = Math.Max(0, depthMetres);
            Cost = cost;
            SourceIndex = sourceIndex;
        }

        public int Index { get; }

        public TerrainWaterDepthClass DepthClass { get; }

        public int DepthMetres { get; }

        public int Cost { get; }

        public int SourceIndex { get; }
    }

    public readonly struct TerrainWaterSourceSnapshot
    {
        public TerrainWaterSourceSnapshot(
            int origin,
            int headElevation,
            long remainingVolume)
        {
            Origin = origin;
            HeadElevation = headElevation;
            RemainingVolume = Math.Max(0L, remainingVolume);
        }

        public int Origin { get; }

        public int HeadElevation { get; }

        public long RemainingVolume { get; }
    }

    public sealed class TerrainWaterSimulation
    {
        private const int MaximumSources = 256;
        private const int MaximumChannelFrontsPerSource = 512;
        private const int ChannelCellsPerBasinCell = 3;
        private const int ChannelTripletSize = 3;
        private const int ConfluenceBasinCellLimit = 4;
        private const double MinimumFlowPriority = 1e-12;

        private readonly TerrainWaterRouting _routing;
        private readonly byte[] _waterMask;
        private readonly byte[] _managedMask;
        private readonly byte[] _material;
        private readonly byte[] _hydroFeature;
        private readonly byte[] _moisture;
        private readonly ushort[] _sourceOwners;
        private readonly Dictionary<ushort, int> _sourceOwnerOrigins =
            new Dictionary<ushort, int>();
        private readonly Dictionary<int, WaterSource> _sourceLookup =
            new Dictionary<int, WaterSource>();
        private readonly List<WaterSource> _sources = new List<WaterSource>();
        private readonly HashSet<ulong> _rewardedConfluences =
            new HashSet<ulong>();
        private readonly TerrainWaterReceiver[] _receiverBuffer =
            new TerrainWaterReceiver[8];
        private readonly List<int> _rechargedCells = new List<int>();
        private readonly HashSet<int> _rechargedCellSet = new HashSet<int>();
        private readonly int _seaLevel;
        private TerrainWaterParameters _parameters;
        private int _managedCellCount;
        private int _nextSource;
        private int _nextSourceOwner = 1;
        private long _confluenceBonusVolume;

        public TerrainWaterSimulation(
            TerrainWaterRouting routing,
            byte[] waterMask,
            byte[] managedMask,
            TerrainWaterParameters parameters,
            int seaLevel = 0,
            byte[] material = null,
            byte[] hydroFeature = null,
            byte[] moisture = null)
        {
            _routing = routing ?? throw new ArgumentNullException(nameof(routing));
            if (waterMask == null || waterMask.Length != routing.CellCount ||
                managedMask == null || managedMask.Length != routing.CellCount)
            {
                throw new ArgumentException("Water masks do not match the routing canvas.");
            }

            _waterMask = waterMask;
            _managedMask = managedMask;
            _material = material != null && material.Length == routing.CellCount
                ? material
                : null;
            _hydroFeature = hydroFeature != null &&
                            hydroFeature.Length == routing.CellCount
                ? hydroFeature
                : null;
            _moisture = moisture != null && moisture.Length == routing.CellCount
                ? moisture
                : null;
            _sourceOwners = new ushort[routing.CellCount];
            _seaLevel = seaLevel;
            _parameters = (parameters ?? new TerrainWaterParameters()).Normalize();
            for (int index = 0; index < managedMask.Length; index++)
            {
                if (managedMask[index] != 0)
                {
                    managedMask[index] = 1;
                    _waterMask[index] = 1;
                    _managedCellCount++;
                }
            }
        }

        public int ManagedCellCount => _managedCellCount;

        public int ActiveSourceCount => _sources.Count(source => source.CanAdvance);

        public int ConfluenceCount => _rewardedConfluences.Count;

        public long ConfluenceBonusVolume => _confluenceBonusVolume;

        public ulong[] CaptureRewardedConfluences()
        {
            return _rewardedConfluences.ToArray();
        }

        public void RestoreConfluenceState(
            IEnumerable<ulong> rewardedConfluences,
            long bonusVolume)
        {
            _rewardedConfluences.Clear();
            if (rewardedConfluences != null)
            {
                foreach (ulong key in rewardedConfluences)
                {
                    _rewardedConfluences.Add(key);
                }
            }

            _confluenceBonusVolume = Math.Max(0L, bonusVolume);
        }

        public int FloodCellLimit => Math.Max(
            1,
            _routing.ValidCellCount * _parameters.MaximumFloodPercent / 100);

        public bool LimitReached => _managedCellCount >= FloodCellLimit;

        public long PendingVolume => _sources
            .Where(source => source.CanAdvance)
            .Sum(source => source.Budget);

        public IReadOnlyList<int> RechargedCells => _rechargedCells;

        public IReadOnlyList<TerrainWaterSourceSnapshot> CaptureActiveSources()
        {
            return _sources
                .Where(source => source.CanAdvance)
                .Select(source => new TerrainWaterSourceSnapshot(
                    source.Origin,
                    source.HeadElevation,
                    source.Budget))
                .ToArray();
        }

        public void UpdateParameters(TerrainWaterParameters parameters)
        {
            TerrainWaterParameters normalized =
                (parameters ?? new TerrainWaterParameters()).Normalize();
            bool routingChanged = normalized.RoutingAlgorithm !=
                                  _parameters.RoutingAlgorithm;
            _parameters = normalized;
            if (!routingChanged)
            {
                return;
            }

            foreach (WaterSource source in _sources)
            {
                source.ResetChannel();
            }
        }

        public bool IsWater(int index)
        {
            return index >= 0 && index < _waterMask.Length && _waterMask[index] != 0;
        }

        public void MarkExternalWater(int index)
        {
            if (index >= 0 && index < _waterMask.Length)
            {
                _waterMask[index] = 1;
            }
        }

        public bool MarkExternalManagedWater(int index)
        {
            if (index < 0 || index >= _waterMask.Length)
            {
                return false;
            }

            bool added = _managedMask[index] == 0;
            MarkManagedWater(index, 0);
            return added;
        }

        public bool MarkExternalDry(int index)
        {
            if (index < 0 || index >= _waterMask.Length)
            {
                return false;
            }

            _waterMask[index] = 0;
            if (_managedMask[index] == 0)
            {
                return false;
            }

            _managedMask[index] = 0;
            _sourceOwners[index] = 0;
            _managedCellCount--;
            return true;
        }

        public bool AddSource(int origin, int headElevation, long volume)
        {
            if (origin < 0 || origin >= _routing.CellCount || volume <= 0 ||
                _routing.Elevation[origin] == TerrainElevationEncoding.NoData)
            {
                return false;
            }

            if (_sourceLookup.TryGetValue(origin, out WaterSource existing))
            {
                existing.HeadElevation = Math.Max(existing.HeadElevation, headElevation);
                existing.HeadElevation = Math.Max(
                    existing.HeadElevation,
                    _routing.FilledElevation[origin]);
                if (!existing.CanReceive || existing.Budget <= 0)
                {
                    existing.ResetChannel();
                }

                existing.Budget = SaturatingAdd(existing.Budget, volume);

                return true;
            }

            if (_sources.Count >= MaximumSources)
            {
                PurgeInactiveSources();
                if (_sources.Count >= MaximumSources)
                {
                    return false;
                }
            }

            WaterSource source = new WaterSource(
                origin,
                Math.Max(headElevation, _routing.FilledElevation[origin]),
                volume,
                _routing.Elevation,
                AllocateSourceOwner(origin));
            _sourceLookup.Add(origin, source);
            _sources.Add(source);
            return true;
        }

        public bool RemoveSource(int origin)
        {
            if (!_sourceLookup.TryGetValue(origin, out WaterSource source))
            {
                return false;
            }

            int index = _sources.IndexOf(source);
            _sourceLookup.Remove(origin);
            if (index < 0)
            {
                return true;
            }

            _sources.RemoveAt(index);
            if (_nextSource > index)
            {
                _nextSource--;
            }

            if (_nextSource >= _sources.Count)
            {
                _nextSource = 0;
            }

            return true;
        }

        public IReadOnlyList<TerrainWaterCellChange> Step(
            int maximumChanges,
            Func<int, bool> canFlood)
        {
            _rechargedCells.Clear();
            _rechargedCellSet.Clear();
            if (maximumChanges <= 0)
            {
                return Array.Empty<TerrainWaterCellChange>();
            }

            List<TerrainWaterCellChange> changes =
                new List<TerrainWaterCellChange>(maximumChanges);
            if (_sources.Count == 0 || LimitReached)
            {
                return changes;
            }

            int attempts = 0;
            int maximumAttempts = Math.Max(64, maximumChanges * 16 + _sources.Count * 4);
            while (changes.Count < maximumChanges && attempts++ < maximumAttempts)
            {
                WaterSource source = _sources[_nextSource++];
                if (_nextSource >= _sources.Count)
                {
                    _nextSource = 0;
                }

                if (!source.CanAdvance || LimitReached)
                {
                    source.Budget = 0;
                    continue;
                }

                Advance(source, canFlood, changes, maximumChanges);
            }

            return changes;
        }

        private void Advance(
            WaterSource source,
            Func<int, bool> canFlood,
            List<TerrainWaterCellChange> changes,
            int maximumChanges)
        {
            if ((source.Channel.Count == 0 ||
                 source.ChannelCellsSinceBasin >= ChannelCellsPerBasinCell) &&
                source.Basin.Count > 0)
            {
                TerrainWaterCellChange? basinChange = AdvanceBasin(source, canFlood);
                if (basinChange.HasValue || source.Channel.Count == 0)
                {
                    if (basinChange.HasValue)
                    {
                        changes.Add(basinChange.Value);
                    }

                    return;
                }
            }

            if (source.Channel.Count == 0)
            {
                if (TryStartTerminalLake(
                        source,
                        source.LastChannelCell,
                        canFlood))
                {
                    TerrainWaterCellChange? terminalChange =
                        AdvanceBasin(source, canFlood);
                    if (terminalChange.HasValue)
                    {
                        changes.Add(terminalChange.Value);
                    }
                }
                else
                {
                    source.Complete();
                }

                return;
            }

            AdvanceChannel(source, canFlood, changes, maximumChanges);
        }

        private void AdvanceChannel(
            WaterSource source,
            Func<int, bool> canFlood,
            List<TerrainWaterCellChange> changes,
            int maximumChanges)
        {
            for (int skip = 0; skip < 64; skip++)
            {
                if (!source.Channel.TryPop(out ChannelFront front))
                {
                    return;
                }

                int candidate = front.Index;

                if (_waterMask[candidate] != 0)
                {
                    if (candidate != source.Origin && _managedMask[candidate] == 0)
                    {
                        source.Complete();
                        return;
                    }

                    RegisterManagedWaterContact(
                        source,
                        candidate,
                        canFlood);
                    RecordRecharge(candidate);
                    if (candidate != source.Origin &&
                        TerrainRiverValleyModel.NormalizeFeature(
                            _hydroFeature?[candidate] ?? 0) ==
                        TerrainHydroFeature.Waterbody)
                    {
                        source.Complete();
                        return;
                    }

                    EnqueueBasin(source, candidate);
                    EnqueueReceivers(source, candidate, front.Priority);
                    continue;
                }

                if (_routing.Elevation[candidate] > source.HeadElevation)
                {
                    source.Channel.Push(front);
                    return;
                }

                if (canFlood != null && !canFlood(candidate))
                {
                    continue;
                }

                int cost = GetCellCost(candidate);
                if (source.Budget < cost || LimitReached)
                {
                    source.Channel.Push(front);
                    return;
                }

                MaterializeChannelTriplet(
                    source,
                    candidate,
                    front.Priority,
                    canFlood,
                    changes,
                    maximumChanges);
                return;
            }
        }

        private void MaterializeChannelTriplet(
            WaterSource source,
            int first,
            double firstPriority,
            Func<int, bool> canFlood,
            List<TerrainWaterCellChange> changes,
            int maximumChanges)
        {
            int current = first;
            double priority = firstPriority;
            for (int part = 0;
                 part < ChannelTripletSize && changes.Count < maximumChanges;
                 part++)
            {
                int cost = GetCellCost(current);
                if (_waterMask[current] != 0 ||
                    _routing.Elevation[current] > source.HeadElevation ||
                    canFlood != null && !canFlood(current) ||
                    source.Budget < cost ||
                    LimitReached)
                {
                    break;
                }

                source.Budget -= cost;
                MarkManagedWater(current, source.OwnerId);
                EnqueueBasin(source, current);
                source.ChannelCellsSinceBasin++;
                source.LastChannelCell = current;
                changes.Add(CreateChange(current, cost, source.Origin));

                int receiver = -1;
                double receiverPriority = 0.0;
                bool continueTriplet =
                    part + 1 < ChannelTripletSize &&
                    changes.Count < maximumChanges &&
                    !LimitReached &&
                    TrySelectTripletReceiver(
                        source,
                        current,
                        priority,
                        canFlood,
                        out receiver,
                        out receiverPriority);
                if (continueTriplet)
                {
                    source.ChannelSeen.Add(receiver);
                }

                EnqueueReceivers(
                    source,
                    current,
                    priority,
                    continueTriplet ? receiver : -1);
                if (!continueTriplet)
                {
                    break;
                }

                current = receiver;
                priority = receiverPriority;
            }
        }

        private bool TrySelectTripletReceiver(
            WaterSource source,
            int current,
            double parentPriority,
            Func<int, bool> canFlood,
            out int selected,
            out double selectedPriority)
        {
            selected = -1;
            selectedPriority = 0.0;
            int count = _routing.GetReceivers(
                current,
                _parameters.RoutingAlgorithm,
                _receiverBuffer);
            SortReceiversByWeight(_receiverBuffer, count);
            for (int index = 0; index < count; index++)
            {
                TerrainWaterReceiver receiver = _receiverBuffer[index];
                int candidate = receiver.Index;
                if (source.ChannelSeen.Contains(candidate) ||
                    _waterMask[candidate] != 0 ||
                    _routing.Elevation[candidate] > source.HeadElevation ||
                    canFlood != null && !canFlood(candidate) ||
                    source.Budget < GetCellCost(candidate))
                {
                    continue;
                }

                double priority = parentPriority * receiver.Weight;
                if (double.IsNaN(priority) || priority <= 0.0)
                {
                    continue;
                }

                selected = candidate;
                selectedPriority = Math.Max(MinimumFlowPriority, priority);
                return true;
            }

            return false;
        }

        private TerrainWaterCellChange? AdvanceBasin(
            WaterSource source,
            Func<int, bool> canFlood)
        {
            while (source.Basin.Count > 0)
            {
                int candidate = source.Basin.Pop();
                if (_waterMask[candidate] != 0)
                {
                    if (_managedMask[candidate] == 0)
                    {
                        source.Complete();
                        return null;
                    }

                    RegisterManagedWaterContact(
                        source,
                        candidate,
                        canFlood);
                    RecordRecharge(candidate);
                    if (source.TerminalLakeActive)
                    {
                        EnqueueTerminalLakeNeighbors(
                            source,
                            candidate,
                            canFlood);
                    }

                    continue;
                }

                if (_routing.Elevation[candidate] > source.HeadElevation)
                {
                    continue;
                }

                if (canFlood != null && !canFlood(candidate))
                {
                    continue;
                }

                int cost = GetCellCost(candidate);
                if (source.Budget < cost || LimitReached)
                {
                    source.Basin.Push(candidate);
                    return null;
                }

                source.Budget -= cost;
                MarkManagedWater(candidate, source.OwnerId);
                if (source.TerminalLakeActive)
                {
                    if (_hydroFeature != null)
                    {
                        _hydroFeature[candidate] =
                            (byte)TerrainHydroFeature.Waterbody;
                    }

                    EnqueueTerminalLakeNeighbors(
                        source,
                        candidate,
                        canFlood);
                }
                else
                {
                    EnqueueBasin(source, candidate);
                }

                source.ChannelCellsSinceBasin = 0;
                return CreateChange(candidate, cost, source.Origin);
            }

            return null;
        }

        private bool TryStartTerminalLake(
            WaterSource source,
            int endpoint,
            Func<int, bool> canFlood)
        {
            if (endpoint < 0 || endpoint >= _routing.CellCount)
            {
                return false;
            }

            bool reachedSink = false;
            int selected = -1;
            int selectedFillDepth = int.MinValue;
            int selectedElevation = int.MaxValue;
            int selectedResistance = int.MaxValue;
            _routing.ForEachNeighbor(endpoint, delegate(int neighbor)
            {
                if (reachedSink)
                {
                    return;
                }

                if (_waterMask[neighbor] != 0)
                {
                    if (neighbor != source.Origin &&
                        !source.ChannelSeen.Contains(neighbor))
                    {
                        reachedSink = true;
                    }

                    return;
                }

                if (_routing.Elevation[neighbor] ==
                        TerrainElevationEncoding.NoData ||
                    _routing.Elevation[neighbor] > source.HeadElevation ||
                    source.ChannelSeen.Contains(neighbor) ||
                    canFlood != null && !canFlood(neighbor))
                {
                    return;
                }

                int fillDepth = _routing.GetFillDepth(neighbor);
                int elevation = _routing.Elevation[neighbor];
                int resistance = _material == null
                    ? 0
                    : TerrainRiverValleyModel.GetFlowResistance(
                        _material[neighbor],
                        _hydroFeature?[neighbor] ?? 0,
                        _moisture?[neighbor] ?? 0);
                if (selected < 0 ||
                    fillDepth > selectedFillDepth ||
                    fillDepth == selectedFillDepth &&
                    (elevation < selectedElevation ||
                     elevation == selectedElevation &&
                     (resistance < selectedResistance ||
                      resistance == selectedResistance && neighbor < selected)))
                {
                    selected = neighbor;
                    selectedFillDepth = fillDepth;
                    selectedElevation = elevation;
                    selectedResistance = resistance;
                }
            });

            if (reachedSink || _routing.IsBoundary(endpoint))
            {
                source.Complete();
                return false;
            }

            if (selected < 0)
            {
                return false;
            }

            int waterLevel = Math.Min(
                source.HeadElevation,
                Math.Max(
                    _routing.Elevation[selected],
                    _routing.FilledElevation[selected]));
            source.BeginTerminalLake(endpoint, waterLevel);
            QueueTerminalLakeCell(source, selected);
            return source.Basin.Count > 0;
        }

        private void EnqueueTerminalLakeNeighbors(
            WaterSource source,
            int center,
            Func<int, bool> canFlood)
        {
            if (!source.TerminalLakeActive)
            {
                return;
            }

            bool reachedSink = false;
            _routing.ForEachNeighbor(center, delegate(int neighbor)
            {
                if (reachedSink)
                {
                    return;
                }

                if (_waterMask[neighbor] != 0)
                {
                    if (_managedMask[neighbor] == 0)
                    {
                        reachedSink = true;
                        return;
                    }

                    if (source.TerminalLakeSeen.Add(neighbor))
                    {
                        RecordRecharge(neighbor);
                        source.BasinSeen.Add(neighbor);
                        source.Basin.Push(neighbor);
                    }

                    return;
                }

                if (_routing.Elevation[neighbor] ==
                        TerrainElevationEncoding.NoData ||
                    _routing.Elevation[neighbor] > source.TerminalWaterLevel ||
                    canFlood != null && !canFlood(neighbor))
                {
                    return;
                }

                QueueTerminalLakeCell(source, neighbor);
            });

            if (reachedSink)
            {
                source.Complete();
            }
        }

        private static void QueueTerminalLakeCell(
            WaterSource source,
            int index)
        {
            if (!source.TerminalLakeSeen.Add(index) ||
                !source.BasinSeen.Add(index))
            {
                return;
            }

            source.Basin.Push(index);
        }

        private void EnqueueBasin(WaterSource source, int center)
        {
            int fillDepth = _routing.GetFillDepth(center);
            if (fillDepth <= 0)
            {
                return;
            }

            short level = _routing.FilledElevation[center];
            _routing.ForEachNeighbor(center, delegate(int neighbor)
            {
                if (_routing.Elevation[neighbor] == TerrainElevationEncoding.NoData ||
                    _routing.FilledElevation[neighbor] != level ||
                    _routing.GetFillDepth(neighbor) <= 0 ||
                    !source.BasinSeen.Add(neighbor))
                {
                    return;
                }

                source.Basin.Push(neighbor);
            });
        }

        private void EnqueueReceivers(
            WaterSource source,
            int current,
            double parentPriority,
            int excludedReceiver = -1)
        {
            int count = _routing.GetReceivers(
                current,
                _parameters.RoutingAlgorithm,
                _receiverBuffer);
            SortReceiversByWeight(_receiverBuffer, count);
            for (int index = 0; index < count; index++)
            {
                TerrainWaterReceiver receiver = _receiverBuffer[index];
                if (receiver.Index == excludedReceiver)
                {
                    continue;
                }

                double priority = parentPriority * receiver.Weight;
                if (double.IsNaN(priority) || priority <= 0.0)
                {
                    continue;
                }

                TryEnqueueChannel(
                    source,
                    receiver.Index,
                    Math.Max(MinimumFlowPriority, priority));
            }
        }

        private static void SortReceiversByWeight(
            TerrainWaterReceiver[] receivers,
            int count)
        {
            for (int index = 1; index < count; index++)
            {
                TerrainWaterReceiver value = receivers[index];
                int position = index - 1;
                while (position >= 0 &&
                       (receivers[position].Weight < value.Weight ||
                        receivers[position].Weight == value.Weight &&
                        receivers[position].Index > value.Index))
                {
                    receivers[position + 1] = receivers[position];
                    position--;
                }

                receivers[position + 1] = value;
            }
        }

        private static bool TryEnqueueChannel(
            WaterSource source,
            int candidate,
            double priority)
        {
            if (candidate < 0 ||
                source.Channel.Count >= MaximumChannelFrontsPerSource ||
                !source.ChannelSeen.Add(candidate))
            {
                return false;
            }

            source.Channel.Push(candidate, priority);
            return true;
        }

        private ushort AllocateSourceOwner(int origin)
        {
            if (_nextSourceOwner > ushort.MaxValue)
            {
                Array.Clear(_sourceOwners, 0, _sourceOwners.Length);
                _sourceOwnerOrigins.Clear();
                _nextSourceOwner = 1;
                foreach (WaterSource source in _sources)
                {
                    source.OwnerId = (ushort)_nextSourceOwner++;
                    _sourceOwnerOrigins[source.OwnerId] = source.Origin;
                }
            }

            ushort owner = (ushort)_nextSourceOwner++;
            _sourceOwnerOrigins[owner] = origin;
            return owner;
        }

        private void RegisterManagedWaterContact(
            WaterSource source,
            int index,
            Func<int, bool> canFlood)
        {
            if (source == null || index < 0 || index >= _managedMask.Length ||
                _managedMask[index] == 0)
            {
                return;
            }

            ushort owner = _sourceOwners[index];
            if (owner == 0)
            {
                _sourceOwners[index] = source.OwnerId;
                return;
            }

            if (owner == source.OwnerId ||
                TerrainRiverValleyModel.NormalizeFeature(
                    _hydroFeature?[index] ?? 0) ==
                TerrainHydroFeature.Waterbody)
            {
                return;
            }

            if (!_sourceOwnerOrigins.TryGetValue(
                    owner,
                    out int otherOrigin))
            {
                _sourceOwners[index] = source.OwnerId;
                return;
            }

            if (otherOrigin == source.Origin)
            {
                _sourceOwners[index] = source.OwnerId;
                return;
            }

            int lower = Math.Min(otherOrigin, source.Origin);
            int upper = Math.Max(otherOrigin, source.Origin);
            ulong key = ((ulong)(uint)lower << 32) | (uint)upper;
            if (_rewardedConfluences.Contains(key))
            {
                return;
            }

            long bonus = EnqueueConfluenceBasin(
                source,
                index,
                canFlood);
            if (bonus <= 0)
            {
                return;
            }

            _rewardedConfluences.Add(key);
            source.Budget = SaturatingAdd(source.Budget, bonus);
            _confluenceBonusVolume = SaturatingAdd(
                _confluenceBonusVolume,
                bonus);
        }

        private long EnqueueConfluenceBasin(
            WaterSource source,
            int center,
            Func<int, bool> canFlood)
        {
            int accepted = 0;
            long bonus = 0L;
            _routing.ForEachNeighbor(center, delegate(int neighbor)
            {
                if (accepted >= ConfluenceBasinCellLimit ||
                    _waterMask[neighbor] != 0 ||
                    _routing.Elevation[neighbor] ==
                        TerrainElevationEncoding.NoData ||
                    _routing.Elevation[neighbor] > source.HeadElevation ||
                    canFlood != null && !canFlood(neighbor) ||
                    !source.BasinSeen.Add(neighbor))
                {
                    return;
                }

                source.Basin.Push(neighbor);
                bonus = SaturatingAdd(bonus, GetCellCost(neighbor));
                accepted++;
            });

            if (accepted > 0)
            {
                source.ChannelCellsSinceBasin = Math.Max(
                    source.ChannelCellsSinceBasin,
                    ChannelCellsPerBasinCell);
            }

            return bonus;
        }

        private int GetCellCost(int index)
        {
            int depthUnits = _routing.GetFillDepth(index) / _routing.VerticalUnit;
            int resistanceCost = _material == null
                ? 0
                : TerrainRiverValleyModel.GetFlowResistance(
                    _material[index],
                    _hydroFeature?[index] ?? 0,
                    _moisture?[index] ?? 0) / 96;
            return 1 + Math.Min(63, depthUnits) + resistanceCost;
        }

        private TerrainWaterCellChange CreateChange(
            int index,
            int cost,
            int sourceIndex)
        {
            int elevation = _routing.Elevation[index];
            bool marine = TerrainWaterDepthModel.TryClassifyElevation(
                elevation,
                _seaLevel,
                out TerrainWaterDepthClass depthClass);
            int depthMetres = marine
                ? TerrainWaterDepthModel.GetDepthMetres(elevation, _seaLevel)
                : TerrainWaterDepthModel.ShallowStorageUnits;
            return new TerrainWaterCellChange(
                index,
                marine ? depthClass : TerrainWaterDepthClass.Shallow,
                depthMetres,
                cost,
                sourceIndex);
        }

        private void MarkManagedWater(int index, ushort owner)
        {
            _waterMask[index] = 1;
            if (_managedMask[index] == 0)
            {
                _managedMask[index] = 1;
                _managedCellCount++;
            }

            if (owner != 0 && _sourceOwners[index] == 0)
            {
                _sourceOwners[index] = owner;
            }
        }

        private void RecordRecharge(int index)
        {
            if (index >= 0 && index < _managedMask.Length &&
                _managedMask[index] != 0 && _rechargedCellSet.Add(index))
            {
                _rechargedCells.Add(index);
            }
        }

        private static long SaturatingAdd(long left, long right)
        {
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private void PurgeInactiveSources()
        {
            for (int index = _sources.Count - 1;
                 index >= 0 && _sources.Count >= MaximumSources;
                 index--)
            {
                WaterSource source = _sources[index];
                if (source.CanAdvance)
                {
                    continue;
                }

                _sourceLookup.Remove(source.Origin);
                _sources.RemoveAt(index);
                if (_nextSource > index)
                {
                    _nextSource--;
                }
            }

            if (_nextSource >= _sources.Count)
            {
                _nextSource = 0;
            }
        }

        private sealed class WaterSource
        {
            public WaterSource(
                int origin,
                int headElevation,
                long budget,
                short[] elevation,
                ushort ownerId)
            {
                Origin = origin;
                HeadElevation = headElevation;
                Budget = budget;
                OwnerId = ownerId;
                Basin = new IndexMinHeap(elevation);
                ResetChannel();
            }

            public int Origin;
            public int HeadElevation;
            public long Budget;
            public ushort OwnerId;
            public int ChannelCellsSinceBasin;
            public int LastChannelCell;
            public bool TerminalLakeActive;
            public int TerminalWaterLevel;
            public bool CanReceive => Channel.Count > 0 || Basin.Count > 0;
            public bool CanAdvance => Budget > 0;
            public readonly HashSet<int> ChannelSeen = new HashSet<int>();
            public readonly HashSet<int> BasinSeen = new HashSet<int>();
            public readonly HashSet<int> TerminalLakeSeen = new HashSet<int>();
            public readonly ChannelMaxHeap Channel = new ChannelMaxHeap();
            public readonly IndexMinHeap Basin;

            public void ResetChannel()
            {
                Channel.Clear();
                ChannelSeen.Clear();
                ChannelSeen.Add(Origin);
                Channel.Push(Origin, 1.0);
                Basin.Clear();
                BasinSeen.Clear();
                BasinSeen.Add(Origin);
                TerminalLakeSeen.Clear();
                TerminalLakeActive = false;
                TerminalWaterLevel = int.MinValue;
                ChannelCellsSinceBasin = 0;
                LastChannelCell = Origin;
            }

            public void BeginTerminalLake(int anchor, int waterLevel)
            {
                Channel.Clear();
                Basin.Clear();
                BasinSeen.Clear();
                TerminalLakeSeen.Clear();
                TerminalLakeActive = true;
                TerminalWaterLevel = waterLevel;
                TerminalLakeSeen.Add(anchor);
                BasinSeen.Add(anchor);
                LastChannelCell = anchor;
            }

            public void Complete()
            {
                Budget = 0;
                Channel.Clear();
                Basin.Clear();
                TerminalLakeActive = false;
            }
        }

        private readonly struct ChannelFront
        {
            public ChannelFront(int index, double priority, long order)
            {
                Index = index;
                Priority = priority;
                Order = order;
            }

            public int Index { get; }

            public double Priority { get; }

            public long Order { get; }
        }

        private sealed class ChannelMaxHeap
        {
            private ChannelFront[] _items = new ChannelFront[16];
            private long _nextOrder;

            public int Count { get; private set; }

            public void Clear()
            {
                Count = 0;
                _nextOrder = 0;
            }

            public void Push(int index, double priority)
            {
                Push(new ChannelFront(index, priority, _nextOrder++));
            }

            public void Push(ChannelFront value)
            {
                if (Count == _items.Length)
                {
                    Array.Resize(ref _items, checked(_items.Length * 2));
                }

                int position = Count++;
                while (position > 0)
                {
                    int parent = (position - 1) / 2;
                    if (Compare(_items[parent], value) >= 0)
                    {
                        break;
                    }

                    _items[position] = _items[parent];
                    position = parent;
                }

                _items[position] = value;
            }

            public bool TryPop(out ChannelFront result)
            {
                if (Count == 0)
                {
                    result = default;
                    return false;
                }

                result = _items[0];
                ChannelFront replacement = _items[--Count];
                if (Count == 0)
                {
                    return true;
                }

                int position = 0;
                while (true)
                {
                    int left = position * 2 + 1;
                    if (left >= Count)
                    {
                        break;
                    }

                    int right = left + 1;
                    int child = right < Count &&
                                Compare(_items[right], _items[left]) > 0
                        ? right
                        : left;
                    if (Compare(replacement, _items[child]) >= 0)
                    {
                        break;
                    }

                    _items[position] = _items[child];
                    position = child;
                }

                _items[position] = replacement;
                return true;
            }

            private static int Compare(ChannelFront left, ChannelFront right)
            {
                int priority = left.Priority.CompareTo(right.Priority);
                if (priority != 0)
                {
                    return priority;
                }

                int order = right.Order.CompareTo(left.Order);
                return order != 0
                    ? order
                    : right.Index.CompareTo(left.Index);
            }
        }

        private sealed class IndexMinHeap
        {
            private readonly short[] _priority;
            private int[] _items = new int[16];

            public IndexMinHeap(short[] priority)
            {
                _priority = priority;
            }

            public int Count { get; private set; }

            public void Clear()
            {
                Count = 0;
            }

            public void Push(int value)
            {
                if (Count == _items.Length)
                {
                    Array.Resize(ref _items, checked(_items.Length * 2));
                }

                int position = Count++;
                while (position > 0)
                {
                    int parent = (position - 1) / 2;
                    if (Compare(_items[parent], value) <= 0)
                    {
                        break;
                    }

                    _items[position] = _items[parent];
                    position = parent;
                }

                _items[position] = value;
            }

            public int Pop()
            {
                int result = _items[0];
                int replacement = _items[--Count];
                if (Count == 0)
                {
                    return result;
                }

                int position = 0;
                while (true)
                {
                    int left = position * 2 + 1;
                    if (left >= Count)
                    {
                        break;
                    }

                    int right = left + 1;
                    int child = right < Count && Compare(_items[right], _items[left]) < 0
                        ? right
                        : left;
                    if (Compare(replacement, _items[child]) <= 0)
                    {
                        break;
                    }

                    _items[position] = _items[child];
                    position = child;
                }

                _items[position] = replacement;
                return result;
            }

            private int Compare(int left, int right)
            {
                int elevation = _priority[left].CompareTo(_priority[right]);
                return elevation != 0 ? elevation : left.CompareTo(right);
            }
        }
    }

    public sealed class TerrainWaterDynamicsModule : ITerrainLabPackageModule
    {
        private const string MetadataPath = "state.json";
        private const string ManagedMaskPath = "managed_water.u8";
        private const string WaterStoragePath = "water_storage.u8";
        private const string RestoreSurfacePath = "restore_surface.u8";
        private const string HydroFeaturePath = "hydro_feature.u8";
        private const string MoisturePath = "moisture.u8";
        private const string ErodibilityPath = "erodibility.u8";
        private const string LocalSlopePath = "local_slope.u8";
        private const string LocalAspectPath = "local_aspect.u8";

        public string Id => "hydrology.water_dynamics";

        public string SchemaVersion => "1.7.0";

        public bool IsRequired => false;

        public void WritePackage(TerrainModuleWriteContext context, TerrainWorldState state)
        {
            TerrainWaterState water = state?.WaterDynamics;
            if (water != null && state != null)
            {
                water.ValidateAndRecount(state.CellCount);
            }

            TerrainWaterMetadata metadata = TerrainWaterMetadata.FromState(state, water);
            context.WriteJson(MetadataPath, metadata);
            if (water?.ManagedMask == null || water.ManagedMask.Length != state.CellCount)
            {
                return;
            }

            context.WriteLayerBytes(
                "hydrology.water_dynamics.managed_mask",
                "hydrology.dynamic_water_mask",
                ManagedMaskPath,
                "uint8",
                water.ManagedMask,
                byte.MaxValue);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.water_storage",
                "hydrology.dynamic_water_storage",
                WaterStoragePath,
                "uint8",
                water.WaterStorage);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.restore_surface",
                "hydrology.dynamic_water_restore_surface",
                RestoreSurfacePath,
                "uint8",
                water.RestoreSurfaceCodes);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.hydro_feature",
                "hydrology.river_waterbody_class",
                HydroFeaturePath,
                "uint8",
                water.HydroFeature,
                byte.MaxValue);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.moisture",
                "hydrology.soil_moisture",
                MoisturePath,
                "uint8",
                water.Moisture);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.erodibility",
                "hydrology.dynamic_erodibility",
                ErodibilityPath,
                "uint8",
                water.Erodibility,
                byte.MaxValue);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.local_slope",
                "hydrology.local_slope_radians_encoded",
                LocalSlopePath,
                "uint8",
                water.LocalSlope,
                TerrainRiverValleyModel.NoDirection);
            context.WriteLayerBytes(
                "hydrology.water_dynamics.local_aspect",
                "hydrology.local_aspect_radians_encoded",
                LocalAspectPath,
                "uint8",
                water.LocalAspect,
                TerrainRiverValleyModel.NoDirection);
        }

        public void ReadPackage(
            TerrainModuleReadContext context,
            TerrainWorldState state,
            TerrainModuleDescriptor descriptor)
        {
            if (!Version.TryParse(descriptor?.SchemaVersion, out Version version) ||
                version.Major != 1)
            {
                throw new InvalidDataException(
                    "Unsupported water dynamics module schema version.");
            }

            TerrainWaterMetadata metadata = context.ReadJson<TerrainWaterMetadata>(MetadataPath);
            if (metadata == null || metadata.Status != "ready")
            {
                state.WaterDynamics = null;
                return;
            }

            if (metadata.Width != state.Width || metadata.Height != state.Height ||
                !string.Equals(metadata.ProjectId, state.ProjectId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Water dynamics metadata does not match the project.");
            }

            byte[] mask = ReadLayer(
                context,
                "hydrology.water_dynamics.managed_mask",
                state.CellCount);
            for (int index = 0; index < mask.Length; index++)
            {
                if (mask[index] > 1)
                {
                    throw new InvalidDataException(
                        "Water dynamics mask contains an invalid value.");
                }
            }

            TerrainWaterState water = TerrainWaterState.Create(state.CellCount);
            water.Enabled = metadata.Enabled;
            water.Parameters = (metadata.Parameters ?? new TerrainWaterParameters()).Normalize();
            water.ManagedMask = mask;
            if (version.Minor >= 2)
            {
                water.WaterStorage = ReadLayer(
                    context,
                    "hydrology.water_dynamics.water_storage",
                    state.CellCount);
                water.RestoreSurfaceCodes = ReadLayer(
                    context,
                    "hydrology.water_dynamics.restore_surface",
                    state.CellCount);
                water.RestoreSurfacePalette = ReadRestoreSurfacePalette(
                    metadata.RestoreSurfacePalette);
                for (int index = 0; index < state.CellCount; index++)
                {
                    if (water.RestoreSurfaceCodes[index] >
                        water.RestoreSurfacePalette.Count ||
                        mask[index] == 0 &&
                        (water.WaterStorage[index] != 0 ||
                         water.RestoreSurfaceCodes[index] != 0) ||
                        mask[index] != 0 && water.WaterStorage[index] == 0)
                    {
                        throw new InvalidDataException(
                            "Water balance layers contain inconsistent values.");
                    }
                }
            }

            if (version.Minor >= 3)
            {
                water.HydroFeature = ReadLayer(
                    context,
                    "hydrology.water_dynamics.hydro_feature",
                    state.CellCount);
                water.Moisture = ReadLayer(
                    context,
                    "hydrology.water_dynamics.moisture",
                    state.CellCount);
                water.Erodibility = ReadLayer(
                    context,
                    "hydrology.water_dynamics.erodibility",
                    state.CellCount);
                water.LocalSlope = ReadLayer(
                    context,
                    "hydrology.water_dynamics.local_slope",
                    state.CellCount);
                water.LocalAspect = ReadLayer(
                    context,
                    "hydrology.water_dynamics.local_aspect",
                    state.CellCount);
                for (int index = 0; index < state.CellCount; index++)
                {
                    if (water.HydroFeature[index] >
                            (byte)TerrainHydroFeature.Waterbody ||
                        water.Erodibility[index] == byte.MaxValue &&
                        state.Elevation[index] != TerrainElevationEncoding.NoData)
                    {
                        throw new InvalidDataException(
                            "River-valley layers contain an invalid value.");
                    }
                }
            }
            else
            {
                for (int index = 0; index < state.CellCount; index++)
                {
                    if (water.ManagedMask[index] != 0)
                    {
                        TerrainRiverValleyModel.ActivateCell(state, water, index);
                    }
                }
            }

            if (version.Minor < 4)
            {
                for (int index = 0; index < state.CellCount; index++)
                {
                    bool active = water.ManagedMask[index] != 0 ||
                                  water.Moisture[index] != 0 ||
                                  TerrainRiverValleyModel.NormalizeFeature(
                                      water.HydroFeature[index]) !=
                                  TerrainHydroFeature.None;
                    if (!active)
                    {
                        water.LocalSlope[index] =
                            TerrainRiverValleyModel.NoDirection;
                        water.LocalAspect[index] =
                            TerrainRiverValleyModel.NoDirection;
                        continue;
                    }

                    TerrainRiverValleyModel.CalculateLocalTerrain(
                        state.Width,
                        state.Height,
                        state.Elevation,
                        index,
                        state.HorizontalMetresPerCell,
                        out water.LocalSlope[index],
                        out water.LocalAspect[index],
                        out _);
                }
            }

            water.TotalInjectedVolume = Math.Max(0, metadata.TotalInjectedVolume);
            water.TotalConsumedVolume = Math.Max(0, metadata.TotalConsumedVolume);
            water.GeyserPulseCount = Math.Max(0, metadata.GeyserPulseCount);
            water.ValidateAndRecount(state.CellCount);
            if (water.ManagedCellCount != metadata.ManagedCellCount)
            {
                throw new InvalidDataException(
                    "Water dynamics managed-cell count is inconsistent.");
            }

            bool terrainAttributesMigrated =
                TerrainRiverValleyModel.EnsureTerrainAttributes(state, water);
            water.IsDirty = terrainAttributesMigrated;
            state.WaterDynamics = water;
        }

        private static List<TerrainSurfaceStamp> ReadRestoreSurfacePalette(
            IList<TerrainWaterSurfaceMetadata> metadata)
        {
            if (metadata == null)
            {
                return new List<TerrainSurfaceStamp>();
            }

            if (metadata.Count > byte.MaxValue)
            {
                throw new InvalidDataException(
                    "Water restore-surface palette is too large.");
            }

            List<TerrainSurfaceStamp> palette =
                new List<TerrainSurfaceStamp>(metadata.Count);
            foreach (TerrainWaterSurfaceMetadata item in metadata)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.MainTypeId))
                {
                    throw new InvalidDataException(
                        "Water restore-surface palette contains an invalid item.");
                }

                palette.Add(new TerrainSurfaceStamp(
                    item.MainTypeId,
                    item.TopTypeId,
                    item.Frozen));
            }

            return palette;
        }

        public void MarkSaved(TerrainWorldState state)
        {
            if (state?.WaterDynamics != null)
            {
                state.WaterDynamics.IsDirty = false;
            }
        }

        private static byte[] ReadLayer(
            TerrainModuleReadContext context,
            string layerId,
            int expectedBytes)
        {
            WbxGeoLayerDescriptor layer = context.Layers.SingleOrDefault(
                item => string.Equals(item.Id, layerId, StringComparison.Ordinal));
            if (layer == null || layer.Module != "hydrology.water_dynamics" ||
                layer.DataType != "uint8" || layer.Entry == null ||
                !layer.Entry.StartsWith(
                    "modules/hydrology.water_dynamics/",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Water dynamics layer descriptor is missing or invalid.");
            }

            string relativePath = layer.Entry.Substring(
                "modules/hydrology.water_dynamics/".Length);
            using (Stream stream = context.OpenEntry(relativePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException(
                        "Water dynamics layer entry was not found.",
                        relativePath);
                }

                byte[] bytes = new byte[expectedBytes];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "Water dynamics layer is shorter than declared.");
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1 ||
                    !string.Equals(
                        WbxGeoPackage.ComputeSha256(bytes),
                        layer.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Water dynamics layer size or checksum is invalid.");
                }

                return bytes;
            }
        }

        private sealed class TerrainWaterMetadata
        {
            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("project_id")]
            public string ProjectId { get; set; }

            [JsonProperty("width")]
            public int Width { get; set; }

            [JsonProperty("height")]
            public int Height { get; set; }

            [JsonProperty("enabled")]
            public bool Enabled { get; set; }

            [JsonProperty("parameters")]
            public TerrainWaterParameters Parameters { get; set; }

            [JsonProperty("managed_cell_count")]
            public int ManagedCellCount { get; set; }

            [JsonProperty("total_injected_volume")]
            public long TotalInjectedVolume { get; set; }

            [JsonProperty("total_consumed_volume")]
            public long TotalConsumedVolume { get; set; }

            [JsonProperty("geyser_pulse_count")]
            public long GeyserPulseCount { get; set; }

            [JsonProperty("restore_surface_palette", NullValueHandling = NullValueHandling.Ignore)]
            public List<TerrainWaterSurfaceMetadata> RestoreSurfacePalette { get; set; }

            public static TerrainWaterMetadata FromState(
                TerrainWorldState state,
                TerrainWaterState water)
            {
                return new TerrainWaterMetadata
                {
                    Status = water?.ManagedMask != null &&
                             water.ManagedMask.Length == state.CellCount
                        ? "ready"
                        : "not_initialized",
                    ProjectId = state.ProjectId,
                    Width = state.Width,
                    Height = state.Height,
                    Enabled = water?.Enabled ?? false,
                    Parameters = water?.Parameters ?? new TerrainWaterParameters(),
                    ManagedCellCount = water?.ManagedCellCount ?? 0,
                    TotalInjectedVolume = water?.TotalInjectedVolume ?? 0,
                    TotalConsumedVolume = water?.TotalConsumedVolume ?? 0,
                    GeyserPulseCount = water?.GeyserPulseCount ?? 0,
                    RestoreSurfacePalette = water?.RestoreSurfacePalette?
                        .Select(TerrainWaterSurfaceMetadata.FromStamp)
                        .ToList()
                };
            }
        }

        private sealed class TerrainWaterSurfaceMetadata
        {
            [JsonProperty("main_type_id")]
            public string MainTypeId { get; set; }

            [JsonProperty("top_type_id")]
            public string TopTypeId { get; set; }

            [JsonProperty("frozen")]
            public bool Frozen { get; set; }

            public static TerrainWaterSurfaceMetadata FromStamp(
                TerrainSurfaceStamp stamp)
            {
                return new TerrainWaterSurfaceMetadata
                {
                    MainTypeId = stamp.MainTypeId,
                    TopTypeId = stamp.TopTypeId,
                    Frozen = stamp.Frozen
                };
            }
        }
    }
}
