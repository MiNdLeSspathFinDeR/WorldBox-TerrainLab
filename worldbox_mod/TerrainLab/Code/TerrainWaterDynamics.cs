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
        Coastal = 1,
        Deep = 2
    }

    public sealed class TerrainWaterParameters
    {
        public const int HardMaximumFloodPercent = 50;

        [JsonProperty("maximum_flood_percent")]
        public int MaximumFloodPercent { get; set; } = HardMaximumFloodPercent;

        [JsonProperty("initial_source_volume")]
        public int InitialSourceVolume { get; set; } = 32;

        [JsonProperty("geyser_pulse_volume")]
        public int GeyserPulseVolume { get; set; } = 2;

        [JsonProperty("cells_per_tick")]
        public int CellsPerTick { get; set; } = 48;

        public TerrainWaterParameters Normalize()
        {
            return new TerrainWaterParameters
            {
                MaximumFloodPercent = Math.Max(
                    1,
                    Math.Min(HardMaximumFloodPercent, MaximumFloodPercent)),
                InitialSourceVolume = Math.Max(1, Math.Min(4096, InitialSourceVolume)),
                GeyserPulseVolume = Math.Max(1, Math.Min(1024, GeyserPulseVolume)),
                CellsPerTick = Math.Max(1, Math.Min(512, CellsPerTick))
            };
        }
    }

    public sealed class TerrainWaterState
    {
        public bool Enabled { get; internal set; }

        public TerrainWaterParameters Parameters { get; internal set; } =
            new TerrainWaterParameters();

        public byte[] ManagedMask { get; internal set; }

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
                ManagedMask = new byte[cellCount]
            };
        }

        internal void ValidateAndRecount(int cellCount)
        {
            if (ManagedMask == null || ManagedMask.Length != cellCount)
            {
                ManagedMask = new byte[cellCount];
            }

            int count = 0;
            for (int index = 0; index < ManagedMask.Length; index++)
            {
                if (ManagedMask[index] == 0)
                {
                    continue;
                }

                ManagedMask[index] = 1;
                count++;
            }

            ManagedCellCount = count;
            Parameters = (Parameters ?? new TerrainWaterParameters()).Normalize();
        }
    }

    public sealed class TerrainWaterRouting
    {
        private static readonly int[] DirectionX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DirectionY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        private TerrainWaterRouting(
            int width,
            int height,
            short[] elevation,
            short[] filledElevation,
            int[] receiver,
            int validCellCount,
            int verticalUnit)
        {
            Width = width;
            Height = height;
            Elevation = elevation;
            FilledElevation = filledElevation;
            Receiver = receiver;
            ValidCellCount = validCellCount;
            VerticalUnit = verticalUnit;
        }

        public int Width { get; }

        public int Height { get; }

        public int CellCount => checked(Width * Height);

        public short[] Elevation { get; }

        public short[] FilledElevation { get; }

        public int[] Receiver { get; }

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
                processed++;
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
                validCount,
                verticalUnit);
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
            int cost,
            int sourceIndex)
        {
            Index = index;
            DepthClass = depthClass;
            Cost = cost;
            SourceIndex = sourceIndex;
        }

        public int Index { get; }

        public TerrainWaterDepthClass DepthClass { get; }

        public int Cost { get; }

        public int SourceIndex { get; }
    }

    public sealed class TerrainWaterSimulation
    {
        private const int MaximumSources = 256;
        private const int ChannelCellsPerBasinCell = 3;

        private readonly TerrainWaterRouting _routing;
        private readonly byte[] _waterMask;
        private readonly byte[] _managedMask;
        private readonly Dictionary<int, WaterSource> _sourceLookup =
            new Dictionary<int, WaterSource>();
        private readonly List<WaterSource> _sources = new List<WaterSource>();
        private TerrainWaterParameters _parameters;
        private int _managedCellCount;
        private int _nextSource;

        public TerrainWaterSimulation(
            TerrainWaterRouting routing,
            byte[] waterMask,
            byte[] managedMask,
            TerrainWaterParameters parameters)
        {
            _routing = routing ?? throw new ArgumentNullException(nameof(routing));
            if (waterMask == null || waterMask.Length != routing.CellCount ||
                managedMask == null || managedMask.Length != routing.CellCount)
            {
                throw new ArgumentException("Water masks do not match the routing canvas.");
            }

            _waterMask = waterMask;
            _managedMask = managedMask;
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

        public int FloodCellLimit => Math.Max(
            1,
            _routing.ValidCellCount * _parameters.MaximumFloodPercent / 100);

        public bool LimitReached => _managedCellCount >= FloodCellLimit;

        public long PendingVolume => _sources
            .Where(source => source.CanAdvance)
            .Sum(source => source.Budget);

        public void UpdateParameters(TerrainWaterParameters parameters)
        {
            _parameters = (parameters ?? new TerrainWaterParameters()).Normalize();
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
                if (existing.CanReceive)
                {
                    existing.Budget = SaturatingAdd(existing.Budget, volume);
                }

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
                _routing.Elevation);
            _sourceLookup.Add(origin, source);
            _sources.Add(source);
            return true;
        }

        public IReadOnlyList<TerrainWaterCellChange> Step(
            int maximumChanges,
            Func<int, bool> canFlood)
        {
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

                TerrainWaterCellChange? change = Advance(source, canFlood);
                if (change.HasValue)
                {
                    changes.Add(change.Value);
                }
            }

            return changes;
        }

        private TerrainWaterCellChange? Advance(
            WaterSource source,
            Func<int, bool> canFlood)
        {
            if ((source.ChannelEnded ||
                 source.ChannelCellsSinceBasin >= ChannelCellsPerBasinCell) &&
                source.Basin.Count > 0)
            {
                TerrainWaterCellChange? basinChange = AdvanceBasin(source, canFlood);
                if (basinChange.HasValue || source.ChannelEnded)
                {
                    return basinChange;
                }
            }

            if (source.ChannelEnded)
            {
                source.Budget = 0;
                return null;
            }

            return AdvanceChannel(source, canFlood);
        }

        private TerrainWaterCellChange? AdvanceChannel(
            WaterSource source,
            Func<int, bool> canFlood)
        {
            for (int skip = 0; skip < 64; skip++)
            {
                int candidate = source.Cursor;
                if (candidate < 0)
                {
                    source.ChannelEnded = true;
                    return null;
                }

                if (!source.ChannelVisited.Add(candidate))
                {
                    source.ChannelEnded = true;
                    return null;
                }

                if (_waterMask[candidate] != 0)
                {
                    if (candidate != source.Origin && _managedMask[candidate] == 0)
                    {
                        source.ChannelEnded = true;
                        return null;
                    }

                    EnqueueBasin(source, candidate);
                    source.Cursor = _routing.Receiver[candidate];
                    continue;
                }

                if (_routing.Elevation[candidate] > source.HeadElevation)
                {
                    return null;
                }

                if (canFlood != null && !canFlood(candidate))
                {
                    int alternative = FindAlternativeReceiver(source, candidate);
                    if (alternative < 0)
                    {
                        return null;
                    }

                    source.Cursor = alternative;
                    continue;
                }

                int cost = GetCellCost(candidate);
                if (source.Budget < cost || LimitReached)
                {
                    return null;
                }

                source.Budget -= cost;
                MarkManagedWater(candidate);
                EnqueueBasin(source, candidate);
                source.Cursor = _routing.Receiver[candidate];
                source.ChannelCellsSinceBasin++;
                return new TerrainWaterCellChange(
                    candidate,
                    TerrainWaterDepthClass.Shallow,
                    cost,
                    source.Origin);
            }

            return null;
        }

        private TerrainWaterCellChange? AdvanceBasin(
            WaterSource source,
            Func<int, bool> canFlood)
        {
            while (source.Basin.Count > 0)
            {
                int candidate = source.Basin.Pop();
                if (_waterMask[candidate] != 0 ||
                    _routing.Elevation[candidate] > source.HeadElevation)
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
                MarkManagedWater(candidate);
                EnqueueBasin(source, candidate);
                source.ChannelCellsSinceBasin = 0;
                return new TerrainWaterCellChange(
                    candidate,
                    GetDepthClass(candidate),
                    cost,
                    source.Origin);
            }

            return null;
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

        private int FindAlternativeReceiver(WaterSource source, int current)
        {
            int best = -1;
            _routing.ForEachNeighbor(current, delegate(int neighbor)
            {
                if (_routing.Elevation[neighbor] == TerrainElevationEncoding.NoData ||
                    _routing.Elevation[neighbor] > source.HeadElevation ||
                    source.ChannelVisited.Contains(neighbor) ||
                    _routing.FilledElevation[neighbor] > _routing.FilledElevation[current])
                {
                    return;
                }

                if (best < 0 ||
                    _routing.FilledElevation[neighbor] < _routing.FilledElevation[best] ||
                    _routing.FilledElevation[neighbor] == _routing.FilledElevation[best] &&
                    (_routing.Elevation[neighbor] < _routing.Elevation[best] ||
                     _routing.Elevation[neighbor] == _routing.Elevation[best] && neighbor < best))
                {
                    best = neighbor;
                }
            });
            return best;
        }

        private int GetCellCost(int index)
        {
            int depthUnits = _routing.GetFillDepth(index) / _routing.VerticalUnit;
            return 1 + Math.Min(63, depthUnits);
        }

        private TerrainWaterDepthClass GetDepthClass(int index)
        {
            int depthUnits = _routing.GetFillDepth(index) / _routing.VerticalUnit;
            if (depthUnits >= 4)
            {
                return TerrainWaterDepthClass.Deep;
            }

            return depthUnits >= 2
                ? TerrainWaterDepthClass.Coastal
                : TerrainWaterDepthClass.Shallow;
        }

        private void MarkManagedWater(int index)
        {
            _waterMask[index] = 1;
            if (_managedMask[index] == 0)
            {
                _managedMask[index] = 1;
                _managedCellCount++;
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
                short[] elevation)
            {
                Origin = origin;
                Cursor = origin;
                HeadElevation = headElevation;
                Budget = budget;
                Basin = new IndexMinHeap(elevation);
                BasinSeen.Add(origin);
            }

            public int Origin;
            public int Cursor;
            public int HeadElevation;
            public long Budget;
            public bool ChannelEnded;
            public int ChannelCellsSinceBasin;
            public bool CanReceive => !ChannelEnded || Basin.Count > 0;
            public bool CanAdvance => Budget > 0 && CanReceive;
            public readonly HashSet<int> ChannelVisited = new HashSet<int>();
            public readonly HashSet<int> BasinSeen = new HashSet<int>();
            public readonly IndexMinHeap Basin;
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

        public string Id => "hydrology.water_dynamics";

        public string SchemaVersion => "1.0.0";

        public bool IsRequired => false;

        public void WritePackage(TerrainModuleWriteContext context, TerrainWorldState state)
        {
            TerrainWaterState water = state?.WaterDynamics;
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
            water.TotalInjectedVolume = Math.Max(0, metadata.TotalInjectedVolume);
            water.TotalConsumedVolume = Math.Max(0, metadata.TotalConsumedVolume);
            water.GeyserPulseCount = Math.Max(0, metadata.GeyserPulseCount);
            water.ValidateAndRecount(state.CellCount);
            if (water.ManagedCellCount != metadata.ManagedCellCount)
            {
                throw new InvalidDataException(
                    "Water dynamics managed-cell count is inconsistent.");
            }

            water.IsDirty = false;
            state.WaterDynamics = water;
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
                    GeyserPulseCount = water?.GeyserPulseCount ?? 0
                };
            }
        }
    }
}
