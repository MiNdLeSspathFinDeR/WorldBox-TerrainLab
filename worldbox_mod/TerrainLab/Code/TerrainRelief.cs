using System;
using System.Collections.Generic;
using System.Threading;

namespace TerrainLab
{
    public enum TerrainLayerAvailability
    {
        Ready,
        Stale,
        Missing
    }

    public sealed class TerrainLayerInfo
    {
        public TerrainLayerInfo(
            string id,
            string group,
            string dataType,
            int? noData,
            TerrainLayerAvailability availability,
            bool editable)
        {
            Id = id;
            Group = group;
            DataType = dataType;
            NoData = noData;
            Availability = availability;
            Editable = editable;
        }

        public string Id { get; }

        public string Group { get; }

        public string DataType { get; }

        public int? NoData { get; }

        public TerrainLayerAvailability Availability { get; }

        public bool Editable { get; }
    }

    public static class TerrainLayerCatalog
    {
        public static IReadOnlyList<TerrainLayerInfo> Build(TerrainWorldState state)
        {
            List<TerrainLayerInfo> layers = new List<TerrainLayerInfo>();
            if (state == null)
            {
                return layers;
            }

            layers.Add(new TerrainLayerInfo(
                "core.elevation",
                "core",
                "int16",
                TerrainElevationEncoding.NoData,
                TerrainLayerAvailability.Ready,
                true));
            layers.Add(new TerrainLayerInfo(
                "core.landform",
                "core",
                "uint8",
                null,
                TerrainLayerAvailability.Ready,
                false));
            layers.Add(new TerrainLayerInfo(
                "core.material",
                "core",
                "uint8",
                null,
                TerrainLayerAvailability.Ready,
                false));
            layers.Add(new TerrainLayerInfo(
                "base.worldbox",
                "core",
                "map.wbox",
                null,
                TerrainLayerAvailability.Ready,
                false));

            TerrainLayerAvailability relief = GetAvailability(
                state.Relief != null,
                state.Relief?.IsCurrent(state) == true);
            layers.Add(new TerrainLayerInfo(
                "relief.slope",
                "relief",
                "uint16/0.1-degree",
                ushort.MaxValue,
                relief,
                false));
            layers.Add(new TerrainLayerInfo(
                "relief.aspect",
                "relief",
                "uint16/0.1-degree",
                ushort.MaxValue,
                relief,
                false));
            layers.Add(new TerrainLayerInfo(
                "relief.hillshade",
                "relief",
                "uint8",
                byte.MaxValue,
                relief,
                false));
            layers.Add(new TerrainLayerInfo(
                "relief.ruggedness",
                "relief",
                "uint16",
                ushort.MaxValue,
                relief,
                false));

            TerrainLayerAvailability hydrology = GetAvailability(
                state.Hydrology != null,
                state.Hydrology?.IsCurrent(state) == true);
            layers.Add(new TerrainLayerInfo(
                "hydrology.filled_elevation",
                "hydrology",
                "int16",
                TerrainElevationEncoding.NoData,
                hydrology,
                false));
            layers.Add(new TerrainLayerInfo(
                "hydrology.flow_direction",
                "hydrology",
                "uint8",
                byte.MaxValue,
                hydrology,
                false));
            layers.Add(new TerrainLayerInfo(
                "hydrology.flow_accumulation",
                "hydrology",
                "uint32",
                0,
                hydrology,
                false));
            layers.Add(new TerrainLayerInfo(
                "hydrology.streams",
                "hydrology",
                "uint8",
                byte.MaxValue,
                hydrology,
                false));
            layers.Add(new TerrainLayerInfo(
                "hydrology.watersheds",
                "hydrology",
                "uint32",
                0,
                hydrology,
                false));
            layers.Add(new TerrainLayerInfo(
                "hydrology.stream_order",
                "hydrology",
                "uint8",
                byte.MaxValue,
                hydrology,
                false));

            TerrainLayerAvailability erosion = GetAvailability(
                state.Erosion != null,
                state.Erosion?.IsCurrent(state) == true);
            layers.Add(new TerrainLayerInfo(
                "erosion.result_elevation",
                "erosion",
                "int16",
                TerrainElevationEncoding.NoData,
                erosion,
                false));
            layers.Add(new TerrainLayerInfo(
                "erosion.net_change",
                "erosion",
                "int32",
                int.MinValue,
                erosion,
                false));
            return layers;
        }

        private static TerrainLayerAvailability GetAvailability(bool exists, bool current)
        {
            if (!exists)
            {
                return TerrainLayerAvailability.Missing;
            }

            return current
                ? TerrainLayerAvailability.Ready
                : TerrainLayerAvailability.Stale;
        }
    }

    public sealed class TerrainReliefStatistics
    {
        public int ValidCellCount { get; internal set; }

        public short MinimumElevation { get; internal set; }

        public short MaximumElevation { get; internal set; }

        public ushort MaximumSlopeTenths { get; internal set; }

        public ushort MaximumRuggedness { get; internal set; }
    }

    public sealed class TerrainReliefResult
    {
        internal TerrainReliefResult(
            string projectId,
            long sourceRevision,
            string sourceSha256,
            DateTime calculatedUtc,
            int width,
            int height,
            ushort[] slopeTenths,
            ushort[] aspectTenths,
            byte[] hillshade,
            ushort[] ruggedness,
            TerrainReliefStatistics statistics)
        {
            ProjectId = projectId;
            SourceRevision = sourceRevision;
            SourceSha256 = sourceSha256;
            CalculatedUtc = calculatedUtc;
            Width = width;
            Height = height;
            SlopeTenths = slopeTenths;
            AspectTenths = aspectTenths;
            Hillshade = hillshade;
            Ruggedness = ruggedness;
            Statistics = statistics;
        }

        public string ProjectId { get; }

        public long SourceRevision { get; }

        public string SourceSha256 { get; }

        public DateTime CalculatedUtc { get; }

        public int Width { get; }

        public int Height { get; }

        public ushort[] SlopeTenths { get; }

        public ushort[] AspectTenths { get; }

        public byte[] Hillshade { get; }

        public ushort[] Ruggedness { get; }

        public TerrainReliefStatistics Statistics { get; }

        public bool IsCurrent(TerrainWorldState state)
        {
            return state != null &&
                   string.Equals(ProjectId, state.ProjectId, StringComparison.Ordinal) &&
                   Width == state.Width && Height == state.Height &&
                   SourceRevision == state.Revision;
        }
    }

    public static class TerrainReliefAnalyzer
    {
        public const string AlgorithmId = "horn-3x3-relief";
        public const string AlgorithmVersion = "1.0.0";

        public static TerrainReliefResult Analyze(
            TerrainWorldState state,
            Action<int> reportProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return Analyze(
                state.ProjectId,
                state.Revision,
                state.Width,
                state.Height,
                state.Elevation,
                reportProgress,
                cancellationToken);
        }

        public static TerrainReliefResult Analyze(
            string projectId,
            long sourceRevision,
            int width,
            int height,
            short[] elevation,
            Action<int> reportProgress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            int count = checked(width * height);
            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new ArgumentException("Relief analysis requires a project id.", nameof(projectId));
            }

            if (elevation == null || elevation.Length != count)
            {
                throw new ArgumentException("Relief DEM does not match the canvas.", nameof(elevation));
            }

            ushort[] slope = new ushort[count];
            ushort[] aspect = new ushort[count];
            byte[] hillshade = new byte[count];
            ushort[] ruggedness = new ushort[count];
            short minimum = short.MaxValue;
            short maximum = short.MinValue;
            ushort maximumSlope = 0;
            ushort maximumRuggedness = 0;
            int validCount = 0;
            const double sunAzimuthRadians = 315.0 * Math.PI / 180.0;
            const double sunAltitudeRadians = 45.0 * Math.PI / 180.0;
            double sunEast = Math.Sin(sunAzimuthRadians) * Math.Cos(sunAltitudeRadians);
            double sunNorth = Math.Cos(sunAzimuthRadians) * Math.Cos(sunAltitudeRadians);
            double sunUp = Math.Sin(sunAltitudeRadians);

            reportProgress?.Invoke(0);
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((y & 15) == 0)
                {
                    reportProgress?.Invoke(y * 100 / Math.Max(1, height));
                }

                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    short center = elevation[index];
                    if (center == TerrainElevationEncoding.NoData)
                    {
                        slope[index] = ushort.MaxValue;
                        aspect[index] = ushort.MaxValue;
                        hillshade[index] = byte.MaxValue;
                        ruggedness[index] = ushort.MaxValue;
                        continue;
                    }

                    validCount++;
                    minimum = Math.Min(minimum, center);
                    maximum = Math.Max(maximum, center);

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
                    ushort slopeTenths = (ushort)Math.Min(
                        900,
                        Math.Round(Math.Atan(gradient) * 1800.0 / Math.PI));
                    slope[index] = slopeTenths;
                    maximumSlope = Math.Max(maximumSlope, slopeTenths);

                    if (gradient < 1e-9)
                    {
                        aspect[index] = ushort.MaxValue;
                    }
                    else
                    {
                        double aspectDegrees =
                            Math.Atan2(-dzdx, -dzdy) * 180.0 / Math.PI;
                        if (aspectDegrees < 0.0)
                        {
                            aspectDegrees += 360.0;
                        }

                        aspect[index] = (ushort)Math.Min(3599, Math.Round(aspectDegrees * 10.0));
                    }

                    double normalLength = Math.Sqrt(dzdx * dzdx + dzdy * dzdy + 1.0);
                    double illumination =
                        (-dzdx * sunEast - dzdy * sunNorth + sunUp) / normalLength;
                    int shade = (int)Math.Round(Math.Max(0.0, illumination) * 254.0);
                    hillshade[index] = (byte)Math.Max(0, Math.Min(254, shade));

                    long differenceSum = 0;
                    differenceSum += Math.Abs((long)northWest - center);
                    differenceSum += Math.Abs((long)north - center);
                    differenceSum += Math.Abs((long)northEast - center);
                    differenceSum += Math.Abs((long)west - center);
                    differenceSum += Math.Abs((long)east - center);
                    differenceSum += Math.Abs((long)southWest - center);
                    differenceSum += Math.Abs((long)south - center);
                    differenceSum += Math.Abs((long)southEast - center);
                    ushort ruggednessValue = (ushort)Math.Min(
                        ushort.MaxValue - 1,
                        (differenceSum + 4) / 8);
                    ruggedness[index] = ruggednessValue;
                    maximumRuggedness = Math.Max(maximumRuggedness, ruggednessValue);
                }
            }

            if (validCount == 0)
            {
                throw new InvalidOperationException("Relief analysis cannot run on an all-NODATA DEM.");
            }

            reportProgress?.Invoke(100);
            return new TerrainReliefResult(
                projectId,
                sourceRevision,
                ComputeSourceSha256(elevation),
                DateTime.UtcNow,
                width,
                height,
                slope,
                aspect,
                hillshade,
                ruggedness,
                new TerrainReliefStatistics
                {
                    ValidCellCount = validCount,
                    MinimumElevation = minimum,
                    MaximumElevation = maximum,
                    MaximumSlopeTenths = maximumSlope,
                    MaximumRuggedness = maximumRuggedness
                });
        }

        internal static string ComputeSourceSha256(short[] elevation)
        {
            if (elevation == null)
            {
                return null;
            }

            byte[] bytes = new byte[checked(elevation.Length * sizeof(short))];
            Buffer.BlockCopy(elevation, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                for (int offset = 0; offset < bytes.Length; offset += sizeof(short))
                {
                    byte first = bytes[offset];
                    bytes[offset] = bytes[offset + 1];
                    bytes[offset + 1] = first;
                }
            }

            return WbxGeoPackage.ComputeSha256(bytes);
        }

        private static short Sample(
            short[] elevation,
            int width,
            int height,
            int x,
            int y,
            short fallback)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(0, Math.Min(height - 1, y));
            short value = elevation[y * width + x];
            return value == TerrainElevationEncoding.NoData ? fallback : value;
        }
    }
}
