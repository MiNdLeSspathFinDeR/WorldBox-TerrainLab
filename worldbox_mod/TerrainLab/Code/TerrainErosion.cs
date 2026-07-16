using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TerrainLab
{
    public sealed class TerrainErosionParameters
    {
        [JsonProperty("iterations")]
        public int Iterations { get; set; } = 8;

        [JsonProperty("flow_strength_percent")]
        public int FlowStrengthPercent { get; set; } = 35;

        [JsonProperty("thermal_strength_percent")]
        public int ThermalStrengthPercent { get; set; } = 15;

        [JsonProperty("talus_threshold")]
        public int TalusThreshold { get; set; } = 4;

        public TerrainErosionParameters Clone()
        {
            return new TerrainErosionParameters
            {
                Iterations = Iterations,
                FlowStrengthPercent = FlowStrengthPercent,
                ThermalStrengthPercent = ThermalStrengthPercent,
                TalusThreshold = TalusThreshold
            };
        }

        public void Validate()
        {
            if (Iterations < 1 || Iterations > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Iterations),
                    "Erosion iterations must be from 1 to 100.");
            }

            if (FlowStrengthPercent < 0 || FlowStrengthPercent > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(FlowStrengthPercent),
                    "Flow strength must be from 0 to 100 percent.");
            }

            if (ThermalStrengthPercent < 0 || ThermalStrengthPercent > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ThermalStrengthPercent),
                    "Thermal strength must be from 0 to 100 percent.");
            }

            if (TalusThreshold < 0 || TalusThreshold > 32767)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TalusThreshold),
                    "Talus threshold must be from 0 to 32767.");
            }
        }
    }

    public sealed class TerrainErosionStatistics
    {
        [JsonProperty("valid_cells")]
        public int ValidCellCount { get; internal set; }

        [JsonProperty("changed_cells")]
        public int ChangedCellCount { get; internal set; }

        [JsonProperty("hydraulic_transport")]
        public long HydraulicTransport { get; internal set; }

        [JsonProperty("thermal_transport")]
        public long ThermalTransport { get; internal set; }

        [JsonProperty("eroded_mass")]
        public long ErodedMass { get; internal set; }

        [JsonProperty("deposited_mass")]
        public long DepositedMass { get; internal set; }

        [JsonProperty("mass_balance")]
        public long MassBalance { get; internal set; }

        [JsonProperty("maximum_cut")]
        public int MaximumCut { get; internal set; }

        [JsonProperty("maximum_fill")]
        public int MaximumFill { get; internal set; }

        [JsonProperty("initial_mass")]
        public long InitialMass { get; internal set; }

        [JsonProperty("final_mass")]
        public long FinalMass { get; internal set; }
    }

    public sealed class TerrainErosionResult
    {
        internal TerrainErosionResult(
            string projectId,
            long sourceRevision,
            string sourceSha256,
            DateTime createdUtc,
            int width,
            int height,
            TerrainErosionParameters parameters,
            short[] resultElevation,
            int[] netChange,
            TerrainErosionStatistics statistics,
            bool isDirty)
        {
            ProjectId = projectId;
            SourceRevision = sourceRevision;
            SourceSha256 = sourceSha256;
            CreatedUtc = createdUtc;
            Width = width;
            Height = height;
            Parameters = parameters;
            ResultElevation = resultElevation;
            NetChange = netChange;
            Statistics = statistics;
            IsDirty = isDirty;
        }

        public string ProjectId { get; }

        public long SourceRevision { get; }

        public string SourceSha256 { get; }

        public DateTime CreatedUtc { get; }

        public int Width { get; }

        public int Height { get; }

        public TerrainErosionParameters Parameters { get; }

        public short[] ResultElevation { get; }

        public int[] NetChange { get; }

        public TerrainErosionStatistics Statistics { get; }

        public bool IsDirty { get; internal set; }

        public bool IsCurrent(TerrainWorldState state)
        {
            return state != null &&
                   string.Equals(ProjectId, state.ProjectId, StringComparison.Ordinal) &&
                   Width == state.Width && Height == state.Height &&
                   SourceRevision == state.Revision;
        }
    }

    public static class TerrainErosionAnalyzer
    {
        public const string AlgorithmId = "fixed-d8-mass-transfer";
        public const string AlgorithmVersion = "1.0.0";

        private static readonly int[] ThermalX = { 1, 0, 1, -1 };
        private static readonly int[] ThermalY = { 0, 1, 1, 1 };
        private static readonly int[] DirectionX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DirectionY = { 0, 1, 1, 1, 0, -1, -1, -1 };

        public static TerrainErosionResult Analyze(
            string projectId,
            long sourceRevision,
            int width,
            int height,
            short[] elevation,
            byte[] landform,
            byte[] flowDirection,
            uint[] flowAccumulation,
            TerrainErosionParameters parameters,
            Action<int> reportProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            int count = checked(width * height);
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new ArgumentException("Erosion requires a project id.", nameof(projectId));
            }

            if (elevation == null || elevation.Length != count ||
                landform == null || landform.Length != count ||
                flowDirection == null || flowDirection.Length != count ||
                flowAccumulation == null || flowAccumulation.Length != count)
            {
                throw new ArgumentException("Erosion source layers do not match the canvas.");
            }

            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            parameters.Validate();
            TerrainErosionParameters frozenParameters = parameters.Clone();
            int[] work = new int[count];
            int validCount = 0;
            long initialMass = 0;
            for (int index = 0; index < count; index++)
            {
                short value = elevation[index];
                work[index] = value;
                if (value != TerrainElevationEncoding.NoData)
                {
                    validCount++;
                    initialMass += value;
                }
            }

            if (validCount == 0)
            {
                throw new InvalidOperationException("Erosion cannot run on an all-NODATA DEM.");
            }

            int[] topologicalOrder = BuildTopologicalOrder(
                elevation,
                flowDirection,
                width,
                height,
                cancellationToken);
            long hydraulicTransport = 0;
            long thermalTransport = 0;
            reportProgress?.Invoke(0);
            for (int iteration = 0; iteration < frozenParameters.Iterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frozenParameters.FlowStrengthPercent > 0)
                {
                    for (int offset = 0; offset < topologicalOrder.Length; offset++)
                    {
                        if ((offset & 8191) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        int source = topologicalOrder[offset];
                        if (elevation[source] == TerrainElevationEncoding.NoData ||
                            landform[source] == (byte)TerrainLandform.Depression)
                        {
                            continue;
                        }

                        byte direction = flowDirection[source];
                        if (direction == byte.MaxValue)
                        {
                            continue;
                        }

                        int sourceX = source % width;
                        int sourceY = source / width;
                        int targetX = sourceX + DirectionX[direction];
                        int targetY = sourceY + DirectionY[direction];
                        if (targetX < 0 || targetX >= width || targetY < 0 || targetY >= height)
                        {
                            continue;
                        }

                        int target = targetY * width + targetX;
                        if (elevation[target] == TerrainElevationEncoding.NoData)
                        {
                            continue;
                        }

                        int excess = work[source] - work[target] - 1;
                        if (excess < 2)
                        {
                            continue;
                        }

                        int flowFactor = 1 + IntegerLog2(Math.Max(1u, flowAccumulation[source]));
                        long scaled = (long)excess * flowFactor *
                                      frozenParameters.FlowStrengthPercent;
                        int amount = (int)Math.Min(int.MaxValue, scaled / 800L);
                        if (amount == 0 && scaled > 0)
                        {
                            amount = 1;
                        }

                        amount = Math.Min(amount, excess / 2);
                        if (TryTransfer(work, source, target, amount))
                        {
                            hydraulicTransport += amount;
                        }
                    }
                }

                if (frozenParameters.ThermalStrengthPercent > 0)
                {
                    for (int y = 0; y < height; y++)
                    {
                        if ((y & 15) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        for (int x = 0; x < width; x++)
                        {
                            int first = y * width + x;
                            if (elevation[first] == TerrainElevationEncoding.NoData)
                            {
                                continue;
                            }

                            for (int direction = 0; direction < ThermalX.Length; direction++)
                            {
                                int otherX = x + ThermalX[direction];
                                int otherY = y + ThermalY[direction];
                                if (otherX < 0 || otherX >= width ||
                                    otherY < 0 || otherY >= height)
                                {
                                    continue;
                                }

                                int second = otherY * width + otherX;
                                if (elevation[second] == TerrainElevationEncoding.NoData)
                                {
                                    continue;
                                }

                                int high = work[first] >= work[second] ? first : second;
                                int low = high == first ? second : first;
                                int excess = work[high] - work[low] -
                                             frozenParameters.TalusThreshold;
                                if (excess < 2)
                                {
                                    continue;
                                }

                                long scaled = (long)excess *
                                              frozenParameters.ThermalStrengthPercent;
                                int amount = (int)(scaled / 400L);
                                if (amount == 0 && scaled > 0)
                                {
                                    amount = 1;
                                }

                                amount = Math.Min(amount, excess / 2);
                                if (TryTransfer(work, high, low, amount))
                                {
                                    thermalTransport += amount;
                                }
                            }
                        }
                    }
                }

                reportProgress?.Invoke((iteration + 1) * 95 / frozenParameters.Iterations);
            }

            short[] resultElevation = new short[count];
            int[] netChange = new int[count];
            int changedCellCount = 0;
            int maximumCut = 0;
            int maximumFill = 0;
            long finalMass = 0;
            for (int index = 0; index < count; index++)
            {
                if (elevation[index] == TerrainElevationEncoding.NoData)
                {
                    resultElevation[index] = TerrainElevationEncoding.NoData;
                    netChange[index] = int.MinValue;
                    continue;
                }

                if (work[index] < short.MinValue || work[index] > short.MaxValue ||
                    work[index] == TerrainElevationEncoding.NoData)
                {
                    throw new InvalidOperationException(
                        "Erosion produced an invalid Int16 elevation.");
                }

                resultElevation[index] = (short)work[index];
                int change = work[index] - elevation[index];
                netChange[index] = change;
                finalMass += work[index];
                if (change != 0)
                {
                    changedCellCount++;
                    maximumCut = Math.Max(maximumCut, -change);
                    maximumFill = Math.Max(maximumFill, change);
                }
            }

            long transported = hydraulicTransport + thermalTransport;
            long balance = finalMass - initialMass;
            if (balance != 0)
            {
                throw new InvalidOperationException(
                    "Erosion mass balance failed: " + balance);
            }

            reportProgress?.Invoke(100);
            return new TerrainErosionResult(
                projectId,
                sourceRevision,
                ComputeSourceSha256(elevation, landform),
                DateTime.UtcNow,
                width,
                height,
                frozenParameters,
                resultElevation,
                netChange,
                new TerrainErosionStatistics
                {
                    ValidCellCount = validCount,
                    ChangedCellCount = changedCellCount,
                    HydraulicTransport = hydraulicTransport,
                    ThermalTransport = thermalTransport,
                    ErodedMass = transported,
                    DepositedMass = transported,
                    MassBalance = balance,
                    MaximumCut = maximumCut,
                    MaximumFill = maximumFill,
                    InitialMass = initialMass,
                    FinalMass = finalMass
                },
                true);
        }

        internal static string ComputeSourceSha256(
            short[] elevation,
            byte[] landform)
        {
            string hydrologySource = TerrainHydrologyAnalyzer.ComputeSourceSha256(
                elevation,
                landform);
            byte[] signature = Encoding.UTF8.GetBytes(
                hydrologySource + ":" + TerrainHydrologyAnalyzer.AlgorithmVersion);
            return WbxGeoPackage.ComputeSha256(signature);
        }

        private static int[] BuildTopologicalOrder(
            short[] elevation,
            byte[] flowDirection,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            int count = elevation.Length;
            byte[] incoming = new byte[count];
            int validCount = 0;
            for (int index = 0; index < count; index++)
            {
                if (elevation[index] == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                validCount++;
                byte direction = flowDirection[index];
                if (direction == byte.MaxValue)
                {
                    continue;
                }

                int x = index % width + DirectionX[direction];
                int y = index / width + DirectionY[direction];
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    throw new InvalidOperationException("D8 receiver lies outside the erosion canvas.");
                }

                int target = y * width + x;
                if (elevation[target] == TerrainElevationEncoding.NoData || incoming[target] >= 8)
                {
                    throw new InvalidOperationException("D8 graph is inconsistent at an erosion receiver.");
                }

                incoming[target]++;
            }

            int[] order = new int[validCount];
            int head = 0;
            int tail = 0;
            for (int index = 0; index < count; index++)
            {
                if (elevation[index] != TerrainElevationEncoding.NoData && incoming[index] == 0)
                {
                    order[tail++] = index;
                }
            }

            while (head < tail)
            {
                if ((head & 8191) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int index = order[head++];
                byte direction = flowDirection[index];
                if (direction == byte.MaxValue)
                {
                    continue;
                }

                int x = index % width + DirectionX[direction];
                int y = index / width + DirectionY[direction];
                int target = y * width + x;
                incoming[target]--;
                if (incoming[target] == 0)
                {
                    order[tail++] = target;
                }
            }

            if (tail != validCount)
            {
                throw new InvalidOperationException("D8 graph contains a cycle.");
            }

            return order;
        }

        private static bool TryTransfer(int[] elevation, int high, int low, int amount)
        {
            if (amount <= 0 || high == low)
            {
                return false;
            }

            long nextHigh = (long)elevation[high] - amount;
            long nextLow = (long)elevation[low] + amount;
            if (nextHigh < short.MinValue || nextHigh > short.MaxValue ||
                nextLow < short.MinValue || nextLow > short.MaxValue ||
                nextHigh == TerrainElevationEncoding.NoData ||
                nextLow == TerrainElevationEncoding.NoData)
            {
                return false;
            }

            elevation[high] = (int)nextHigh;
            elevation[low] = (int)nextLow;
            return true;
        }

        private static int IntegerLog2(uint value)
        {
            int result = 0;
            while (value > 1)
            {
                value >>= 1;
                result++;
            }

            return result;
        }

    }

    public sealed class TerrainErosionModule : ITerrainLabPackageModule
    {
        private const string MetadataPath = "analysis.json";
        private const string ResultElevationPath = "result_elevation.i16";
        private const string NetChangePath = "net_change.i32";

        private readonly object _sync = new object();
        private Task<TerrainErosionResult> _task;
        private CancellationTokenSource _cancellation;
        private int _generation;
        private int _taskGeneration;
        private int _progressPercent;
        private string _lastError;

        public string Id => "erosion.hydraulic";

        public string SchemaVersion => "1.0.0";

        public bool IsRequired => false;

        public bool IsRunning
        {
            get
            {
                lock (_sync)
                {
                    return _task != null;
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
            TerrainErosionParameters parameters,
            out string error)
        {
            error = null;
            if (state == null || state.Hydrology == null || !state.Hydrology.IsCurrent(state))
            {
                error = "Erosion requires current D8 hydrology.";
                return false;
            }

            try
            {
                parameters?.Validate();
                if (parameters == null)
                {
                    throw new ArgumentNullException(nameof(parameters));
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            lock (_sync)
            {
                if (_task != null)
                {
                    error = "Erosion analysis is already running.";
                    return false;
                }

                int generation = ++_generation;
                _taskGeneration = generation;
                _progressPercent = 0;
                _lastError = null;
                _cancellation = new CancellationTokenSource();
                CancellationToken token = _cancellation.Token;
                string projectId = state.ProjectId;
                long revision = state.Revision;
                int width = state.Width;
                int height = state.Height;
                short[] elevation = (short[])state.Elevation.Clone();
                byte[] landform = (byte[])state.Landform.Clone();
                byte[] direction = (byte[])state.Hydrology.FlowDirection.Clone();
                uint[] accumulation = (uint[])state.Hydrology.FlowAccumulation.Clone();
                TerrainErosionParameters frozen = parameters.Clone();
                _task = Task.Run(
                    () => TerrainErosionAnalyzer.Analyze(
                        projectId,
                        revision,
                        width,
                        height,
                        elevation,
                        landform,
                        direction,
                        accumulation,
                        frozen,
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
            Task<TerrainErosionResult> task;
            int generation;
            lock (_sync)
            {
                task = _task;
                generation = _taskGeneration;
                if (task == null || !task.IsCompleted)
                {
                    return false;
                }
            }

            TerrainErosionResult result = null;
            string error = null;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                error = "Erosion analysis was cancelled.";
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            lock (_sync)
            {
                if (!ReferenceEquals(task, _task) || generation != _taskGeneration)
                {
                    return false;
                }

                _task = null;
                _cancellation?.Dispose();
                _cancellation = null;
                _lastError = error;
            }

            if (result != null)
            {
                if (result.IsCurrent(state))
                {
                    state.Erosion = result;
                }
                else
                {
                    lock (_sync)
                    {
                        _lastError = "DEM changed while erosion was running; result discarded.";
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

        public void MarkSaved(TerrainWorldState state)
        {
            if (state?.Erosion != null && state.Erosion.IsCurrent(state))
            {
                state.Erosion.IsDirty = false;
            }
        }

        public void WritePackage(TerrainModuleWriteContext context, TerrainWorldState state)
        {
            TerrainErosionResult result = state?.Erosion;
            string status = result != null && result.IsCurrent(state) ? "ready" : "not_computed";
            TerrainErosionMetadata metadata = TerrainErosionMetadata.FromResult(status, result);
            context.WriteJson(MetadataPath, metadata);
            if (status != "ready")
            {
                return;
            }

            string sourceHash = TerrainErosionAnalyzer.ComputeSourceSha256(
                state.Elevation,
                state.Landform);
            if (!string.Equals(sourceHash, result.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Erosion source checksum is stale.");
            }

            context.WriteLayerBytes(
                "erosion.result_elevation",
                "erosion.result_elevation",
                ResultElevationPath,
                "int16",
                ToLittleEndianBytes(result.ResultElevation),
                TerrainElevationEncoding.NoData);
            context.WriteLayerBytes(
                "erosion.net_change",
                "erosion.net_elevation_change",
                NetChangePath,
                "int32",
                ToLittleEndianBytes(result.NetChange),
                int.MinValue);
        }

        public void ReadPackage(
            TerrainModuleReadContext context,
            TerrainWorldState state,
            TerrainModuleDescriptor descriptor)
        {
            if (!Version.TryParse(descriptor?.SchemaVersion, out Version version) ||
                version.Major != 1)
            {
                throw new InvalidDataException("Unsupported erosion module schema version.");
            }

            TerrainErosionMetadata metadata = context.ReadJson<TerrainErosionMetadata>(MetadataPath);
            if (metadata == null || metadata.Status != "ready")
            {
                state.Erosion = null;
                return;
            }

            if (metadata.ProjectId != state.ProjectId || metadata.Width != state.Width ||
                metadata.Height != state.Height || metadata.Parameters == null ||
                metadata.Statistics == null ||
                metadata.Algorithm != TerrainErosionAnalyzer.AlgorithmId ||
                !Version.TryParse(metadata.AlgorithmVersion, out Version algorithmVersion) ||
                algorithmVersion.Major != 1)
            {
                throw new InvalidDataException("Erosion metadata does not match the project.");
            }

            metadata.Parameters.Validate();
            string sourceHash = TerrainErosionAnalyzer.ComputeSourceSha256(
                state.Elevation,
                state.Landform);
            if (!string.Equals(sourceHash, metadata.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Erosion source checksum is stale.");
            }

            int count = state.CellCount;
            short[] resultElevation = ToInt16Array(ReadLayer(
                context,
                "erosion.result_elevation",
                "int16",
                checked(count * sizeof(short))));
            int[] netChange = ToInt32Array(ReadLayer(
                context,
                "erosion.net_change",
                "int32",
                checked(count * sizeof(int))));
            ValidateResult(state.Elevation, resultElevation, netChange, metadata.Statistics);
            state.Erosion = new TerrainErosionResult(
                state.ProjectId,
                state.Revision,
                sourceHash,
                metadata.CreatedUtc,
                state.Width,
                state.Height,
                metadata.Parameters,
                resultElevation,
                netChange,
                metadata.Statistics,
                false);
        }

        private static void ValidateResult(
            short[] source,
            short[] result,
            int[] netChange,
            TerrainErosionStatistics statistics)
        {
            long sourceMass = 0;
            long resultMass = 0;
            int valid = 0;
            int changed = 0;
            int maximumCut = 0;
            int maximumFill = 0;
            for (int index = 0; index < source.Length; index++)
            {
                if (source[index] == TerrainElevationEncoding.NoData)
                {
                    if (result[index] != TerrainElevationEncoding.NoData ||
                        netChange[index] != int.MinValue)
                    {
                        throw new InvalidDataException("Erosion NODATA mask changed.");
                    }

                    continue;
                }

                valid++;
                if (result[index] == TerrainElevationEncoding.NoData ||
                    netChange[index] != result[index] - source[index])
                {
                    throw new InvalidDataException("Erosion net-change layer is inconsistent.");
                }

                sourceMass += source[index];
                resultMass += result[index];
                changed += source[index] == result[index] ? 0 : 1;
                maximumCut = Math.Max(maximumCut, -netChange[index]);
                maximumFill = Math.Max(maximumFill, netChange[index]);
            }

            if (sourceMass != resultMass || statistics.MassBalance != 0 ||
                statistics.InitialMass != sourceMass || statistics.FinalMass != resultMass ||
                statistics.ValidCellCount != valid || statistics.ChangedCellCount != changed ||
                statistics.MaximumCut != maximumCut || statistics.MaximumFill != maximumFill ||
                statistics.HydraulicTransport < 0 || statistics.ThermalTransport < 0 ||
                statistics.HydraulicTransport >
                    long.MaxValue - statistics.ThermalTransport ||
                statistics.ErodedMass != statistics.DepositedMass ||
                statistics.ErodedMass !=
                    statistics.HydraulicTransport + statistics.ThermalTransport)
            {
                throw new InvalidDataException("Erosion mass-balance metadata is inconsistent.");
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
                descriptor.Module != "erosion.hydraulic" ||
                descriptor.ByteOrder != "little-endian" ||
                descriptor.RowOrder != "south-to-north" ||
                string.IsNullOrWhiteSpace(descriptor.Entry) ||
                !descriptor.Entry.StartsWith(
                    "modules/erosion.hydraulic/",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Erosion layer descriptor is invalid: " + layerId);
            }

            string relativePath = descriptor.Entry.Substring(
                "modules/erosion.hydraulic/".Length);
            byte[] bytes = new byte[expectedLength];
            using (Stream stream = context.OpenEntry(relativePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("Erosion layer entry is missing.", relativePath);
                }

                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Erosion layer is truncated: " + layerId);
                    }

                    offset += read;
                }

                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Erosion layer is larger than expected: " + layerId);
                }
            }

            if (!string.Equals(
                WbxGeoPackage.ComputeSha256(bytes),
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Erosion layer checksum failed: " + layerId);
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

        private static byte[] ToLittleEndianBytes(int[] values)
        {
            byte[] bytes = new byte[checked(values.Length * sizeof(int))];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                ReverseElements(bytes, sizeof(int));
            }

            return bytes;
        }

        private static short[] ToInt16Array(byte[] bytes)
        {
            byte[] source = ToHostEndian(bytes, sizeof(short));
            short[] values = new short[source.Length / sizeof(short)];
            Buffer.BlockCopy(source, 0, values, 0, source.Length);
            return values;
        }

        private static int[] ToInt32Array(byte[] bytes)
        {
            byte[] source = ToHostEndian(bytes, sizeof(int));
            int[] values = new int[source.Length / sizeof(int)];
            Buffer.BlockCopy(source, 0, values, 0, source.Length);
            return values;
        }

        private static byte[] ToHostEndian(byte[] bytes, int elementSize)
        {
            if (BitConverter.IsLittleEndian)
            {
                return bytes;
            }

            byte[] clone = (byte[])bytes.Clone();
            ReverseElements(clone, elementSize);
            return clone;
        }

        private static void ReverseElements(byte[] bytes, int elementSize)
        {
            for (int offset = 0; offset < bytes.Length; offset += elementSize)
            {
                Array.Reverse(bytes, offset, elementSize);
            }
        }

        private sealed class TerrainErosionMetadata
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

            [JsonProperty("parameters", NullValueHandling = NullValueHandling.Ignore)]
            public TerrainErosionParameters Parameters { get; set; }

            [JsonProperty("statistics", NullValueHandling = NullValueHandling.Ignore)]
            public TerrainErosionStatistics Statistics { get; set; }

            public static TerrainErosionMetadata FromResult(
                string status,
                TerrainErosionResult result)
            {
                return new TerrainErosionMetadata
                {
                    Status = status,
                    Algorithm = TerrainErosionAnalyzer.AlgorithmId,
                    AlgorithmVersion = TerrainErosionAnalyzer.AlgorithmVersion,
                    ProjectId = result?.ProjectId,
                    SourceRevision = result?.SourceRevision ?? 0,
                    SourceSha256 = result?.SourceSha256,
                    CreatedUtc = result?.CreatedUtc ?? DateTime.UtcNow,
                    Width = result?.Width ?? 0,
                    Height = result?.Height ?? 0,
                    Parameters = result?.Parameters,
                    Statistics = result?.Statistics
                };
            }
        }
    }
}
