using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TerrainLab
{
    public enum TerrainFlowDirection : byte
    {
        East = 0,
        NorthEast = 1,
        North = 2,
        NorthWest = 3,
        West = 4,
        SouthWest = 5,
        South = 6,
        SouthEast = 7,
        None = byte.MaxValue
    }

    public sealed class TerrainHydrologyStatistics
    {
        [JsonProperty("valid_cells")]
        public int ValidCellCount { get; internal set; }

        [JsonProperty("outlet_cells")]
        public int OutletCellCount { get; internal set; }

        [JsonProperty("filled_cells")]
        public int FilledCellCount { get; internal set; }

        [JsonProperty("maximum_fill_depth")]
        public int MaximumFillDepth { get; internal set; }

        [JsonProperty("stream_cells")]
        public int StreamCellCount { get; internal set; }

        [JsonProperty("maximum_accumulation")]
        public uint MaximumAccumulation { get; internal set; }

        [JsonProperty("watersheds")]
        public int WatershedCount { get; internal set; }

        [JsonProperty("maximum_stream_order")]
        public byte MaximumStreamOrder { get; internal set; }
    }

    public sealed class TerrainHydrologyResult
    {
        internal TerrainHydrologyResult(
            string projectId,
            long sourceRevision,
            string sourceSha256,
            DateTime createdUtc,
            int width,
            int height,
            int streamThreshold,
            short[] filledElevation,
            byte[] flowDirection,
            uint[] flowAccumulation,
            byte[] streamMask,
            uint[] watershed,
            byte[] streamOrder,
            TerrainHydrologyStatistics statistics,
            bool isDirty)
        {
            ProjectId = projectId;
            SourceRevision = sourceRevision;
            SourceSha256 = sourceSha256;
            CreatedUtc = createdUtc;
            Width = width;
            Height = height;
            StreamThreshold = streamThreshold;
            FilledElevation = filledElevation;
            FlowDirection = flowDirection;
            FlowAccumulation = flowAccumulation;
            StreamMask = streamMask;
            Watershed = watershed;
            StreamOrder = streamOrder;
            Statistics = statistics;
            IsDirty = isDirty;
        }

        public string ProjectId { get; }

        public long SourceRevision { get; }

        public string SourceSha256 { get; }

        public DateTime CreatedUtc { get; }

        public int Width { get; }

        public int Height { get; }

        public int StreamThreshold { get; internal set; }

        public short[] FilledElevation { get; }

        public byte[] FlowDirection { get; }

        public uint[] FlowAccumulation { get; }

        public byte[] StreamMask { get; internal set; }

        public uint[] Watershed { get; }

        public byte[] StreamOrder { get; internal set; }

        public TerrainHydrologyStatistics Statistics { get; }

        public bool IsDirty { get; internal set; }

        public int CellCount => checked(Width * Height);

        public bool IsCurrent(TerrainWorldState state)
        {
            return state != null &&
                   string.Equals(ProjectId, state.ProjectId, StringComparison.Ordinal) &&
                   Width == state.Width &&
                   Height == state.Height &&
                   SourceRevision == state.Revision;
        }

        public bool TryGetCell(
            int x,
            int y,
            out short filledElevation,
            out TerrainFlowDirection direction,
            out uint accumulation,
            out bool isStream)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                filledElevation = TerrainElevationEncoding.NoData;
                direction = TerrainFlowDirection.None;
                accumulation = 0;
                isStream = false;
                return false;
            }

            int index = checked(y * Width + x);
            filledElevation = FilledElevation[index];
            direction = (TerrainFlowDirection)FlowDirection[index];
            accumulation = FlowAccumulation[index];
            isStream = StreamMask[index] == 1;
            return filledElevation != TerrainElevationEncoding.NoData;
        }

        public bool TryGetAdvancedCell(
            int x,
            int y,
            out uint watershed,
            out byte streamOrder)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                watershed = 0;
                streamOrder = byte.MaxValue;
                return false;
            }

            int index = checked(y * Width + x);
            watershed = Watershed[index];
            streamOrder = StreamOrder[index];
            return FilledElevation[index] != TerrainElevationEncoding.NoData;
        }
    }

    public static class TerrainHydrologyAnalyzer
    {
        public const string AlgorithmId = "priority-flood-d8";
        public const string AlgorithmVersion = "1.1.0";

        private static readonly int[] DirectionX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DirectionY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        public static int GetDefaultStreamThreshold(int cellCount)
        {
            return Math.Max(32, cellCount / 4096);
        }

        public static TerrainHydrologyResult Analyze(
            string projectId,
            long sourceRevision,
            int width,
            int height,
            short[] elevation,
            byte[] landform,
            int streamThreshold,
            Action<int> reportProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            int count = checked(width * height);
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new ArgumentException("Hydrology requires a project id.", nameof(projectId));
            }

            if (elevation == null || elevation.Length != count ||
                landform == null || landform.Length != count)
            {
                throw new ArgumentException("Hydrology source layers do not match the canvas.");
            }

            if (streamThreshold < 1 || streamThreshold > count)
            {
                throw new ArgumentOutOfRangeException(nameof(streamThreshold));
            }

            reportProgress?.Invoke(0);
            short[] filled = (short[])elevation.Clone();
            byte[] flowDirection = Enumerable.Repeat(byte.MaxValue, count).ToArray();
            byte[] visited = new byte[count];
            int[] traversalOrder = new int[count];
            int traversalCount = 0;
            int validCount = 0;
            int outletCount = 0;
            CellMinHeap queue = new CellMinHeap(filled, Math.Min(count, 4096));

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (elevation[index] == TerrainElevationEncoding.NoData)
                    {
                        continue;
                    }

                    validCount++;
                    if (!IsOutletCell(x, y, index, width, height, elevation, landform))
                    {
                        continue;
                    }

                    visited[index] = 1;
                    queue.Push(index);
                    outletCount++;
                }
            }

            if (validCount == 0)
            {
                throw new InvalidOperationException("Hydrology cannot run on an all-NODATA DEM.");
            }

            if (queue.Count == 0)
            {
                throw new InvalidOperationException("Hydrology could not find a drainage outlet.");
            }

            int processed = 0;
            while (queue.Count > 0)
            {
                if ((processed & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reportProgress?.Invoke(Math.Min(60, processed * 60 / validCount));
                }

                int current = queue.Pop();
                traversalOrder[traversalCount++] = current;
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

                    flowDirection[neighbor] = (byte)((direction + 4) & 7);
                    queue.Push(neighbor);
                }
            }

            if (traversalCount != validCount)
            {
                throw new InvalidOperationException("Hydrology did not visit every valid DEM cell.");
            }

            uint[] accumulation = new uint[count];
            int filledCellCount = 0;
            int maximumFillDepth = 0;
            for (int index = 0; index < count; index++)
            {
                if (elevation[index] == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                accumulation[index] = 1;
                int depth = (int)filled[index] - elevation[index];
                if (depth > 0)
                {
                    filledCellCount++;
                    maximumFillDepth = Math.Max(maximumFillDepth, depth);
                }
            }

            uint maximumAccumulation = 1;
            for (int offset = traversalCount - 1; offset >= 0; offset--)
            {
                if ((offset & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int completed = traversalCount - offset;
                    reportProgress?.Invoke(60 + completed * 30 / validCount);
                }

                int index = traversalOrder[offset];
                uint value = accumulation[index];
                maximumAccumulation = Math.Max(maximumAccumulation, value);
                byte direction = flowDirection[index];
                if (direction == byte.MaxValue)
                {
                    continue;
                }

                int x = index % width;
                int y = index / width;
                int targetX = x + DirectionX[direction];
                int targetY = y + DirectionY[direction];
                int target = targetY * width + targetX;
                ulong combined = (ulong)accumulation[target] + value;
                accumulation[target] = combined > uint.MaxValue
                    ? uint.MaxValue
                    : (uint)combined;
                maximumAccumulation = Math.Max(maximumAccumulation, accumulation[target]);
            }

            byte[] streams = BuildStreamMask(
                elevation,
                landform,
                accumulation,
                streamThreshold,
                out int streamCellCount,
                cancellationToken);
            uint[] watersheds = BuildWatersheds(
                elevation,
                landform,
                flowDirection,
                width,
                height,
                out int watershedCount,
                cancellationToken);
            byte[] streamOrder = BuildStreamOrder(
                elevation,
                flowDirection,
                streams,
                width,
                height,
                out byte maximumStreamOrder,
                cancellationToken);
            reportProgress?.Invoke(100);

            return new TerrainHydrologyResult(
                projectId,
                sourceRevision,
                ComputeSourceSha256(elevation, landform),
                DateTime.UtcNow,
                width,
                height,
                streamThreshold,
                filled,
                flowDirection,
                accumulation,
                streams,
                watersheds,
                streamOrder,
                new TerrainHydrologyStatistics
                {
                    ValidCellCount = validCount,
                    OutletCellCount = outletCount,
                    FilledCellCount = filledCellCount,
                    MaximumFillDepth = maximumFillDepth,
                    StreamCellCount = streamCellCount,
                    MaximumAccumulation = maximumAccumulation,
                    WatershedCount = watershedCount,
                    MaximumStreamOrder = maximumStreamOrder
                },
                true);
        }

        internal static void RebuildStreams(
            TerrainWorldState state,
            TerrainHydrologyResult result,
            int streamThreshold)
        {
            if (state == null || result == null || !result.IsCurrent(state))
            {
                throw new InvalidOperationException("Hydrology result is stale.");
            }

            if (streamThreshold < 1 || streamThreshold > state.CellCount)
            {
                throw new ArgumentOutOfRangeException(nameof(streamThreshold));
            }

            result.StreamMask = BuildStreamMask(
                state.Elevation,
                state.Landform,
                result.FlowAccumulation,
                streamThreshold,
                out int streamCellCount,
                CancellationToken.None);
            result.StreamOrder = BuildStreamOrder(
                state.Elevation,
                result.FlowDirection,
                result.StreamMask,
                state.Width,
                state.Height,
                out byte maximumStreamOrder,
                CancellationToken.None);
            result.StreamThreshold = streamThreshold;
            result.Statistics.StreamCellCount = streamCellCount;
            result.Statistics.MaximumStreamOrder = maximumStreamOrder;
            result.IsDirty = true;
        }

        internal static uint[] RebuildWatersheds(
            short[] elevation,
            byte[] landform,
            byte[] flowDirection,
            int width,
            int height,
            out int watershedCount)
        {
            return BuildWatersheds(
                elevation,
                landform,
                flowDirection,
                width,
                height,
                out watershedCount,
                CancellationToken.None);
        }

        internal static string ComputeSourceSha256(short[] elevation, byte[] landform)
        {
            byte[] bytes = new byte[checked(elevation.Length * sizeof(short) + landform.Length)];
            Buffer.BlockCopy(elevation, 0, bytes, 0, elevation.Length * sizeof(short));
            if (!BitConverter.IsLittleEndian)
            {
                ReverseElements(bytes, elevation.Length * sizeof(short), sizeof(short));
            }

            Buffer.BlockCopy(
                landform,
                0,
                bytes,
                elevation.Length * sizeof(short),
                landform.Length);
            return WbxGeoPackage.ComputeSha256(bytes);
        }

        private static byte[] BuildStreamMask(
            short[] elevation,
            byte[] landform,
            uint[] accumulation,
            int threshold,
            out int streamCellCount,
            CancellationToken cancellationToken)
        {
            byte[] streams = new byte[elevation.Length];
            streamCellCount = 0;
            for (int index = 0; index < elevation.Length; index++)
            {
                if ((index & 8191) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (elevation[index] == TerrainElevationEncoding.NoData)
                {
                    streams[index] = byte.MaxValue;
                    continue;
                }

                bool isWater = landform[index] == (byte)TerrainLandform.Depression;
                if (!isWater && accumulation[index] >= threshold)
                {
                    streams[index] = 1;
                    streamCellCount++;
                }
            }

            return streams;
        }

        private static uint[] BuildWatersheds(
            short[] elevation,
            byte[] landform,
            byte[] flowDirection,
            int width,
            int height,
            out int watershedCount,
            CancellationToken cancellationToken)
        {
            uint[] watershed = new uint[elevation.Length];
            int[] stack = new int[elevation.Length];
            uint nextId = 1;
            for (int index = 0; index < elevation.Length; index++)
            {
                if ((index & 8191) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (elevation[index] == TerrainElevationEncoding.NoData ||
                    flowDirection[index] != byte.MaxValue || watershed[index] != 0)
                {
                    continue;
                }

                uint id = nextId++;
                watershed[index] = id;
                if (landform[index] != (byte)TerrainLandform.Depression)
                {
                    continue;
                }

                int stackCount = 1;
                stack[0] = index;
                while (stackCount > 0)
                {
                    int current = stack[--stackCount];
                    int currentX = current % width;
                    int currentY = current / width;
                    for (int direction = 0; direction < DirectionX.Length; direction++)
                    {
                        int x = currentX + DirectionX[direction];
                        int y = currentY + DirectionY[direction];
                        if (x < 0 || x >= width || y < 0 || y >= height)
                        {
                            continue;
                        }

                        int neighbor = y * width + x;
                        if (watershed[neighbor] == 0 &&
                            flowDirection[neighbor] == byte.MaxValue &&
                            elevation[neighbor] != TerrainElevationEncoding.NoData &&
                            landform[neighbor] == (byte)TerrainLandform.Depression)
                        {
                            watershed[neighbor] = id;
                            stack[stackCount++] = neighbor;
                        }
                    }
                }
            }

            watershedCount = checked((int)(nextId - 1));
            for (int start = 0; start < elevation.Length; start++)
            {
                if ((start & 8191) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (elevation[start] == TerrainElevationEncoding.NoData ||
                    watershed[start] != 0)
                {
                    continue;
                }

                int pathCount = 0;
                int index = start;
                while (watershed[index] == 0)
                {
                    if (pathCount >= stack.Length)
                    {
                        throw new InvalidOperationException(
                            "Hydrology flow graph contains a cycle.");
                    }

                    stack[pathCount++] = index;
                    byte direction = flowDirection[index];
                    if (direction == byte.MaxValue)
                    {
                        throw new InvalidOperationException(
                            "Hydrology outlet is missing a watershed id.");
                    }

                    int x = index % width + DirectionX[direction];
                    int y = index / width + DirectionY[direction];
                    if (x < 0 || x >= width || y < 0 || y >= height)
                    {
                        throw new InvalidOperationException(
                            "Hydrology receiver lies outside the canvas.");
                    }

                    index = y * width + x;
                }

                uint id = watershed[index];
                while (pathCount > 0)
                {
                    watershed[stack[--pathCount]] = id;
                }
            }

            return watershed;
        }

        internal static byte[] BuildStreamOrder(
            short[] elevation,
            byte[] flowDirection,
            byte[] streams,
            int width,
            int height,
            out byte maximumOrder,
            CancellationToken cancellationToken)
        {
            int count = checked(width * height);
            byte[] order = new byte[count];
            int[] incoming = new int[count];
            byte[] maximumIncoming = new byte[count];
            byte[] maximumIncomingCount = new byte[count];
            int[] queue = new int[count];
            int queueHead = 0;
            int queueTail = 0;

            for (int index = 0; index < count; index++)
            {
                if (elevation[index] == TerrainElevationEncoding.NoData)
                {
                    order[index] = byte.MaxValue;
                    continue;
                }

                if (streams[index] != 1)
                {
                    continue;
                }

                byte direction = flowDirection[index];
                if (direction == byte.MaxValue)
                {
                    continue;
                }

                int x = index % width + DirectionX[direction];
                int y = index / width + DirectionY[direction];
                int receiver = y * width + x;
                if (streams[receiver] == 1)
                {
                    incoming[receiver]++;
                }
            }

            for (int index = 0; index < count; index++)
            {
                if (streams[index] == 1 && incoming[index] == 0)
                {
                    queue[queueTail++] = index;
                }
            }

            maximumOrder = 0;
            int processed = 0;
            while (queueHead < queueTail)
            {
                if ((processed++ & 8191) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int index = queue[queueHead++];
                byte value = maximumIncoming[index] == 0
                    ? (byte)1
                    : maximumIncomingCount[index] >= 2
                        ? (byte)Math.Min(byte.MaxValue - 1, maximumIncoming[index] + 1)
                        : maximumIncoming[index];
                order[index] = value;
                maximumOrder = Math.Max(maximumOrder, value);

                byte direction = flowDirection[index];
                if (direction == byte.MaxValue)
                {
                    continue;
                }

                int x = index % width + DirectionX[direction];
                int y = index / width + DirectionY[direction];
                int receiver = y * width + x;
                if (streams[receiver] != 1)
                {
                    continue;
                }

                if (value > maximumIncoming[receiver])
                {
                    maximumIncoming[receiver] = value;
                    maximumIncomingCount[receiver] = 1;
                }
                else if (value == maximumIncoming[receiver] &&
                         maximumIncomingCount[receiver] < byte.MaxValue)
                {
                    maximumIncomingCount[receiver]++;
                }

                incoming[receiver]--;
                if (incoming[receiver] == 0)
                {
                    queue[queueTail++] = receiver;
                }
            }

            return order;
        }


        private static bool IsOutletCell(
            int x,
            int y,
            int index,
            int width,
            int height,
            short[] elevation,
            byte[] landform)
        {
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1 ||
                landform[index] == (byte)TerrainLandform.Depression)
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

        private static void ReverseElements(byte[] bytes, int length, int elementSize)
        {
            for (int offset = 0; offset < length; offset += elementSize)
            {
                Array.Reverse(bytes, offset, elementSize);
            }
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
                if (Count == 0)
                {
                    throw new InvalidOperationException("Hydrology priority queue is empty.");
                }

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

    public sealed class TerrainHydrologyModule : ITerrainLabPackageModule
    {
        private const string MetadataPath = "analysis.json";
        private const string FilledElevationPath = "filled_elevation.i16";
        private const string FlowDirectionPath = "flow_direction.u8";
        private const string FlowAccumulationPath = "flow_accumulation.u32";
        private const string StreamsPath = "streams.u8";
        private const string WatershedsPath = "watersheds.u32";
        private const string StreamOrderPath = "stream_order.u8";

        private static readonly int[] DirectionX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DirectionY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        private readonly object _sync = new object();
        private Task<TerrainHydrologyResult> _analysisTask;
        private CancellationTokenSource _cancellation;
        private int _generation;
        private int _taskGeneration;
        private int _progressPercent;
        private string _lastError;

        public string Id => "hydrology";

        public string SchemaVersion => "1.1.0";

        public bool IsRequired => false;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _analysisTask != null;
                }
            }
        }

        public int ProgressPercent => Volatile.Read(ref _progressPercent);

        public string LastError
        {
            get
            {
                lock (_sync)
                {
                    return _lastError;
                }
            }
        }

        public bool TryStartAnalysis(
            TerrainWorldState state,
            int streamThreshold,
            out string error)
        {
            error = null;
            if (state == null)
            {
                error = "Hydrology requires an active TerrainLab project.";
                return false;
            }

            if (streamThreshold < 1 || streamThreshold > state.CellCount)
            {
                error = "Stream threshold must be between 1 and the project cell count.";
                return false;
            }

            lock (_sync)
            {
                if (_analysisTask != null)
                {
                    error = "Hydrology analysis is already running.";
                    return false;
                }

                short[] elevation = (short[])state.Elevation.Clone();
                byte[] landform = (byte[])state.Landform.Clone();
                string projectId = state.ProjectId;
                long revision = state.Revision;
                int width = state.Width;
                int height = state.Height;
                int generation = ++_generation;
                _taskGeneration = generation;
                _progressPercent = 0;
                _lastError = null;
                _cancellation = new CancellationTokenSource();
                CancellationToken token = _cancellation.Token;
                _analysisTask = Task.Run(
                    () => TerrainHydrologyAnalyzer.Analyze(
                        projectId,
                        revision,
                        width,
                        height,
                        elevation,
                        landform,
                        streamThreshold,
                        value =>
                        {
                            if (Volatile.Read(ref _generation) == generation)
                            {
                                Interlocked.Exchange(ref _progressPercent, value);
                            }
                        },
                        token),
                    token);
                return true;
            }
        }

        public bool Poll(TerrainWorldState state)
        {
            Task<TerrainHydrologyResult> task;
            int generation;
            lock (_sync)
            {
                task = _analysisTask;
                generation = _taskGeneration;
                if (task == null || !task.IsCompleted)
                {
                    return false;
                }
            }

            TerrainHydrologyResult result = null;
            string error = null;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                error = "Hydrology analysis was cancelled.";
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            lock (_sync)
            {
                if (!ReferenceEquals(task, _analysisTask) || generation != _taskGeneration)
                {
                    return false;
                }

                _analysisTask = null;
                _cancellation?.Dispose();
                _cancellation = null;
                _lastError = error;
            }

            if (result != null)
            {
                if (result.IsCurrent(state))
                {
                    state.Hydrology = result;
                }
                else
                {
                    lock (_sync)
                    {
                        _lastError = "DEM changed while hydrology was running; result discarded.";
                    }
                }
            }

            return true;
        }

        public void Cancel()
        {
            lock (_sync)
            {
                _cancellation?.Cancel();
            }
        }

        public bool TrySetStreamThreshold(
            TerrainWorldState state,
            int streamThreshold,
            out string error)
        {
            error = null;
            try
            {
                TerrainHydrologyAnalyzer.RebuildStreams(
                    state,
                    state?.Hydrology,
                    streamThreshold);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public void MarkSaved(TerrainWorldState state)
        {
            if (state?.Hydrology != null && state.Hydrology.IsCurrent(state))
            {
                state.Hydrology.IsDirty = false;
            }
        }

        public void WritePackage(TerrainModuleWriteContext context, TerrainWorldState state)
        {
            TerrainHydrologyResult result = state?.Hydrology;
            string status = "not_computed";
            if (result != null && result.IsCurrent(state))
            {
                string sourceHash = TerrainHydrologyAnalyzer.ComputeSourceSha256(
                    state.Elevation,
                    state.Landform);
                status = string.Equals(
                    sourceHash,
                    result.SourceSha256,
                    StringComparison.OrdinalIgnoreCase)
                    ? "ready"
                    : "stale";
            }

            TerrainHydrologyMetadata metadata = TerrainHydrologyMetadata.FromResult(
                status,
                result);
            context.WriteJson(MetadataPath, metadata);
            if (status != "ready")
            {
                return;
            }

            context.WriteLayerBytes(
                "hydrology.filled_elevation",
                "hydrology.filled_elevation",
                FilledElevationPath,
                "int16",
                ToLittleEndianBytes(result.FilledElevation),
                TerrainElevationEncoding.NoData);
            context.WriteLayerBytes(
                "hydrology.flow_direction",
                "hydrology.flow_direction.d8",
                FlowDirectionPath,
                "uint8",
                result.FlowDirection,
                byte.MaxValue);
            context.WriteLayerBytes(
                "hydrology.flow_accumulation",
                "hydrology.flow_accumulation.cells",
                FlowAccumulationPath,
                "uint32",
                ToLittleEndianBytes(result.FlowAccumulation),
                0);
            context.WriteLayerBytes(
                "hydrology.streams",
                "hydrology.stream_mask",
                StreamsPath,
                "uint8",
                result.StreamMask,
                byte.MaxValue);
            context.WriteLayerBytes(
                "hydrology.watersheds",
                "hydrology.watershed_id",
                WatershedsPath,
                "uint32",
                ToLittleEndianBytes(result.Watershed),
                0);
            context.WriteLayerBytes(
                "hydrology.stream_order",
                "hydrology.stream_order.strahler",
                StreamOrderPath,
                "uint8",
                result.StreamOrder,
                byte.MaxValue);
        }

        public void ReadPackage(
            TerrainModuleReadContext context,
            TerrainWorldState state,
            TerrainModuleDescriptor descriptor)
        {
            if (!Version.TryParse(descriptor?.SchemaVersion, out Version schemaVersion) ||
                schemaVersion.Major != 1)
            {
                throw new InvalidDataException("Unsupported hydrology module schema version.");
            }

            TerrainHydrologyMetadata metadata = context.ReadJson<TerrainHydrologyMetadata>(
                MetadataPath);
            if (metadata == null || metadata.Status != "ready")
            {
                state.Hydrology = null;
                return;
            }

            if (metadata.Width != state.Width || metadata.Height != state.Height ||
                !string.Equals(metadata.ProjectId, state.ProjectId, StringComparison.Ordinal) ||
                metadata.Algorithm != TerrainHydrologyAnalyzer.AlgorithmId ||
                !Version.TryParse(metadata.AlgorithmVersion, out Version algorithmVersion) ||
                algorithmVersion.Major != 1)
            {
                throw new InvalidDataException("Hydrology metadata does not match the project.");
            }

            string sourceHash = TerrainHydrologyAnalyzer.ComputeSourceSha256(
                state.Elevation,
                state.Landform);
            if (!string.Equals(
                sourceHash,
                metadata.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Hydrology source DEM checksum is stale.");
            }

            int count = state.CellCount;
            if (metadata.Statistics == null ||
                metadata.StreamThreshold < 1 || metadata.StreamThreshold > count)
            {
                throw new InvalidDataException("Hydrology metadata is incomplete.");
            }

            short[] filled = ToInt16Array(ReadLayer(
                context,
                "hydrology.filled_elevation",
                "int16",
                checked(count * sizeof(short))));
            byte[] direction = ReadLayer(
                context,
                "hydrology.flow_direction",
                "uint8",
                count);
            uint[] accumulation = ToUInt32Array(ReadLayer(
                context,
                "hydrology.flow_accumulation",
                "uint32",
                checked(count * sizeof(uint))));
            byte[] streams = ReadLayer(
                context,
                "hydrology.streams",
                "uint8",
                count);
            uint[] watersheds;
            byte[] streamOrder;
            if (schemaVersion.Minor >= 1)
            {
                watersheds = ToUInt32Array(ReadLayer(
                    context,
                    "hydrology.watersheds",
                    "uint32",
                    checked(count * sizeof(uint))));
                streamOrder = ReadLayer(
                    context,
                    "hydrology.stream_order",
                    "uint8",
                    count);
            }
            else
            {
                watersheds = TerrainHydrologyAnalyzer.RebuildWatersheds(
                    state.Elevation,
                    state.Landform,
                    direction,
                    state.Width,
                    state.Height,
                    out int watershedCount);
                streamOrder = TerrainHydrologyAnalyzer.BuildStreamOrder(
                    state.Elevation,
                    direction,
                    streams,
                    state.Width,
                    state.Height,
                    out byte maximumOrder,
                    CancellationToken.None);
                metadata.Statistics.WatershedCount = watershedCount;
                metadata.Statistics.MaximumStreamOrder = maximumOrder;
            }

            ValidateResult(
                state,
                metadata.StreamThreshold,
                filled,
                direction,
                accumulation,
                streams,
                watersheds,
                streamOrder,
                metadata.Statistics);

            state.Hydrology = new TerrainHydrologyResult(
                state.ProjectId,
                state.Revision,
                metadata.SourceSha256,
                metadata.CreatedUtc,
                state.Width,
                state.Height,
                metadata.StreamThreshold,
                filled,
                direction,
                accumulation,
                streams,
                watersheds,
                streamOrder,
                metadata.Statistics,
                false);
        }

        private static void ValidateResult(
            TerrainWorldState state,
            int streamThreshold,
            short[] filled,
            byte[] direction,
            uint[] accumulation,
            byte[] streams,
            uint[] watersheds,
            byte[] streamOrder,
            TerrainHydrologyStatistics statistics)
        {
            int count = state.CellCount;
            if (statistics.ValidCellCount < 1 || statistics.ValidCellCount > count ||
                statistics.WatershedCount < 1 ||
                statistics.WatershedCount > statistics.ValidCellCount)
            {
                throw new InvalidDataException("Hydrology watershed statistics are invalid.");
            }

            bool[] seenWatersheds = new bool[statistics.WatershedCount + 1];
            int validCount = 0;
            int outletCount = 0;
            int filledCount = 0;
            int maximumFillDepth = 0;
            int streamCount = 0;
            uint maximumAccumulation = 0;
            byte maximumOrder = 0;
            int seenWatershedCount = 0;
            for (int index = 0; index < count; index++)
            {
                short source = state.Elevation[index];
                if (source == TerrainElevationEncoding.NoData)
                {
                    if (filled[index] != TerrainElevationEncoding.NoData ||
                        direction[index] != byte.MaxValue || accumulation[index] != 0 ||
                        streams[index] != byte.MaxValue || watersheds[index] != 0 ||
                        streamOrder[index] != byte.MaxValue)
                    {
                        throw new InvalidDataException("Hydrology NODATA mask is inconsistent.");
                    }

                    continue;
                }

                validCount++;
                if (filled[index] == TerrainElevationEncoding.NoData || filled[index] < source ||
                    accumulation[index] < 1 ||
                    accumulation[index] > (uint)statistics.ValidCellCount ||
                    watersheds[index] == 0 || watersheds[index] > statistics.WatershedCount)
                {
                    throw new InvalidDataException("Hydrology cell values are invalid.");
                }

                uint watershed = watersheds[index];
                if (!seenWatersheds[watershed])
                {
                    seenWatersheds[watershed] = true;
                    seenWatershedCount++;
                }

                int fillDepth = filled[index] - source;
                if (fillDepth > 0)
                {
                    filledCount++;
                    maximumFillDepth = Math.Max(maximumFillDepth, fillDepth);
                }

                maximumAccumulation = Math.Max(maximumAccumulation, accumulation[index]);
                bool expectedStream =
                    state.Landform[index] != (byte)TerrainLandform.Depression &&
                    accumulation[index] >= streamThreshold;
                if (streams[index] != (expectedStream ? (byte)1 : (byte)0))
                {
                    throw new InvalidDataException("Hydrology stream mask is inconsistent.");
                }

                if (expectedStream)
                {
                    if (streamOrder[index] == 0 || streamOrder[index] == byte.MaxValue)
                    {
                        throw new InvalidDataException("Hydrology stream order is invalid.");
                    }

                    streamCount++;
                    maximumOrder = Math.Max(maximumOrder, streamOrder[index]);
                }
                else if (streamOrder[index] != 0)
                {
                    throw new InvalidDataException("Off-stream hydrology order must be zero.");
                }

                byte flow = direction[index];
                if (flow == byte.MaxValue)
                {
                    outletCount++;
                    continue;
                }

                if (flow >= DirectionX.Length)
                {
                    throw new InvalidDataException("Hydrology D8 direction is invalid.");
                }

                int x = index % state.Width + DirectionX[flow];
                int y = index / state.Width + DirectionY[flow];
                if (x < 0 || x >= state.Width || y < 0 || y >= state.Height)
                {
                    throw new InvalidDataException("Hydrology D8 receiver is outside the canvas.");
                }

                int receiver = y * state.Width + x;
                if (state.Elevation[receiver] == TerrainElevationEncoding.NoData ||
                    filled[receiver] > filled[index] ||
                    accumulation[receiver] <= accumulation[index] ||
                    watersheds[receiver] != watersheds[index] ||
                    streams[receiver] == 1 && streamOrder[receiver] < streamOrder[index])
                {
                    throw new InvalidDataException("Hydrology D8 receiver is inconsistent.");
                }
            }

            ValidateAcyclicDirections(state, direction);
            if (validCount != statistics.ValidCellCount ||
                outletCount != statistics.OutletCellCount ||
                filledCount != statistics.FilledCellCount ||
                maximumFillDepth != statistics.MaximumFillDepth ||
                streamCount != statistics.StreamCellCount ||
                maximumAccumulation != statistics.MaximumAccumulation ||
                seenWatershedCount != statistics.WatershedCount ||
                maximumOrder != statistics.MaximumStreamOrder)
            {
                throw new InvalidDataException("Hydrology statistics are inconsistent.");
            }
        }

        private static void ValidateAcyclicDirections(
            TerrainWorldState state,
            byte[] direction)
        {
            byte[] visit = new byte[state.CellCount];
            int[] path = new int[state.CellCount];
            for (int start = 0; start < state.CellCount; start++)
            {
                if (state.Elevation[start] == TerrainElevationEncoding.NoData || visit[start] != 0)
                {
                    continue;
                }

                int pathCount = 0;
                int index = start;
                while (visit[index] == 0)
                {
                    visit[index] = 1;
                    path[pathCount++] = index;
                    byte flow = direction[index];
                    if (flow == byte.MaxValue)
                    {
                        break;
                    }

                    int x = index % state.Width + DirectionX[flow];
                    int y = index / state.Width + DirectionY[flow];
                    index = y * state.Width + x;
                }

                if (visit[index] == 1 && direction[index] != byte.MaxValue)
                {
                    throw new InvalidDataException("Hydrology D8 graph contains a cycle.");
                }

                while (pathCount > 0)
                {
                    visit[path[--pathCount]] = 2;
                }
            }
        }

        private static byte[] ReadLayer(
            TerrainModuleReadContext context,
            string layerId,
            string dataType,
            int expectedLength)
        {
            WbxGeoLayerDescriptor descriptor = context.Layers.FirstOrDefault(
                layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal));
            if (descriptor == null || descriptor.DataType != dataType ||
                descriptor.Module != "hydrology" ||
                descriptor.ByteOrder != "little-endian" ||
                descriptor.RowOrder != "south-to-north" ||
                string.IsNullOrWhiteSpace(descriptor.Entry) ||
                !descriptor.Entry.StartsWith("modules/hydrology/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Hydrology layer descriptor is invalid: " + layerId);
            }

            string relativePath = descriptor.Entry.Substring("modules/hydrology/".Length);
            byte[] bytes = new byte[expectedLength];
            using (Stream stream = context.OpenEntry(relativePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("Hydrology layer entry is missing.", relativePath);
                }

                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Hydrology layer is truncated: " + layerId);
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Hydrology layer is larger than expected: " + layerId);
                }
            }

            if (!string.Equals(
                WbxGeoPackage.ComputeSha256(bytes),
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Hydrology layer checksum failed: " + layerId);
            }

            return bytes;
        }

        private static byte[] ToLittleEndianBytes(short[] values)
        {
            byte[] bytes = new byte[checked(values.Length * sizeof(short))];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                ReverseElements(bytes, sizeof(short));
            }

            return bytes;
        }

        private static byte[] ToLittleEndianBytes(uint[] values)
        {
            byte[] bytes = new byte[checked(values.Length * sizeof(uint))];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                ReverseElements(bytes, sizeof(uint));
            }

            return bytes;
        }

        private static short[] ToInt16Array(byte[] bytes)
        {
            byte[] source = bytes;
            if (!BitConverter.IsLittleEndian)
            {
                source = (byte[])bytes.Clone();
                ReverseElements(source, sizeof(short));
            }

            short[] values = new short[source.Length / sizeof(short)];
            Buffer.BlockCopy(source, 0, values, 0, source.Length);
            return values;
        }

        private static uint[] ToUInt32Array(byte[] bytes)
        {
            byte[] source = bytes;
            if (!BitConverter.IsLittleEndian)
            {
                source = (byte[])bytes.Clone();
                ReverseElements(source, sizeof(uint));
            }

            uint[] values = new uint[source.Length / sizeof(uint)];
            Buffer.BlockCopy(source, 0, values, 0, source.Length);
            return values;
        }

        private static void ReverseElements(byte[] bytes, int elementSize)
        {
            for (int offset = 0; offset < bytes.Length; offset += elementSize)
            {
                Array.Reverse(bytes, offset, elementSize);
            }
        }

        private sealed class TerrainHydrologyMetadata
        {
            [JsonProperty("status")]
            public string Status { get; set; }

            [JsonProperty("algorithm")]
            public string Algorithm { get; set; }

            [JsonProperty("algorithm_version")]
            public string AlgorithmVersion { get; set; }

            [JsonProperty("project_id", NullValueHandling = NullValueHandling.Ignore)]
            public string ProjectId { get; set; }

            [JsonProperty("source_revision")]
            public long SourceRevision { get; set; }

            [JsonProperty("source_sha256", NullValueHandling = NullValueHandling.Ignore)]
            public string SourceSha256 { get; set; }

            [JsonProperty("created_utc")]
            public DateTime CreatedUtc { get; set; }

            [JsonProperty("width")]
            public int Width { get; set; }

            [JsonProperty("height")]
            public int Height { get; set; }

            [JsonProperty("stream_threshold")]
            public int StreamThreshold { get; set; }

            [JsonProperty("statistics", NullValueHandling = NullValueHandling.Ignore)]
            public TerrainHydrologyStatistics Statistics { get; set; }

            public static TerrainHydrologyMetadata FromResult(
                string status,
                TerrainHydrologyResult result)
            {
                return new TerrainHydrologyMetadata
                {
                    Status = status,
                    Algorithm = TerrainHydrologyAnalyzer.AlgorithmId,
                    AlgorithmVersion = TerrainHydrologyAnalyzer.AlgorithmVersion,
                    ProjectId = result?.ProjectId,
                    SourceRevision = result?.SourceRevision ?? 0,
                    SourceSha256 = result?.SourceSha256,
                    CreatedUtc = result?.CreatedUtc ?? DateTime.UtcNow,
                    Width = result?.Width ?? 0,
                    Height = result?.Height ?? 0,
                    StreamThreshold = result?.StreamThreshold ?? 0,
                    Statistics = result?.Statistics
                };
            }
        }
    }
}
