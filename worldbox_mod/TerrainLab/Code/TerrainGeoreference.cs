using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TerrainLab
{
    public sealed class TerrainWgs84ControlPoint
    {
        [JsonProperty("local_x")]
        public double LocalX { get; set; }

        [JsonProperty("local_y")]
        public double LocalY { get; set; }

        [JsonProperty("source_x")]
        public double SourceX { get; set; }

        [JsonProperty("source_y")]
        public double SourceY { get; set; }

        [JsonProperty("longitude")]
        public double Longitude { get; set; }

        [JsonProperty("latitude")]
        public double Latitude { get; set; }
    }

    public sealed class TerrainRasterGeoreference
    {
        public const string FormatId = "terrainlab-raster-georeference";
        public const string SchemaVersionId = "1.0.0";
        public const string MapSidecarFileName = "terrainlab-georeference.json";
        public const double WorldBoxMetresPerCell = 1000d;
        public const int MaximumSidecarBytes = 2 * 1024 * 1024;

        [JsonProperty("format")]
        public string Format { get; set; } = FormatId;

        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; } = SchemaVersionId;

        [JsonProperty("source_file_name")]
        public string SourceFileName { get; set; }

        [JsonProperty("source_width")]
        public int SourceWidth { get; set; }

        [JsonProperty("source_height")]
        public int SourceHeight { get; set; }

        [JsonProperty("raster_width")]
        public int RasterWidth { get; set; }

        [JsonProperty("raster_height")]
        public int RasterHeight { get; set; }

        [JsonProperty("source_raster_to_crs")]
        public double[] SourceRasterToCrs { get; set; }

        [JsonProperty("raster_to_crs")]
        public double[] RasterToCrs { get; set; }

        [JsonProperty("worldbox_cell_to_crs")]
        public double[] WorldBoxCellToCrs { get; set; }

        [JsonProperty("worldbox_metre_to_crs")]
        public double[] WorldBoxMetreToCrs { get; set; }

        [JsonProperty("crs_to_worldbox_cell")]
        public double[] CrsToWorldBoxCell { get; set; }

        [JsonProperty("crs_to_worldbox_metre")]
        public double[] CrsToWorldBoxMetre { get; set; }

        [JsonProperty("worldbox_metres_per_cell")]
        public double WorldBoxMetresPerCellValue { get; set; } =
            WorldBoxMetresPerCell;

        [JsonProperty("crs_wkt")]
        public string CrsWkt { get; set; }

        [JsonProperty("crs_projjson")]
        public string CrsProjJson { get; set; }

        [JsonProperty("epsg")]
        public int? Epsg { get; set; }

        [JsonProperty("crs_kind")]
        public string CrsKind { get; set; }

        [JsonProperty("pixel_interpretation")]
        public string PixelInterpretation { get; set; }

        [JsonProperty("vertical_epsg")]
        public int? VerticalEpsg { get; set; }

        [JsonProperty("vertical_crs_name")]
        public string VerticalCrsName { get; set; }

        [JsonProperty("geo_key_directory")]
        public ushort[] GeoKeyDirectory { get; set; }

        [JsonProperty("geo_double_params")]
        public double[] GeoDoubleParams { get; set; }

        [JsonProperty("geo_ascii_params")]
        public string GeoAsciiParams { get; set; }

        [JsonProperty("wgs84_epsg")]
        public int Wgs84Epsg { get; set; } = 4326;

        [JsonProperty("wgs84_wkt")]
        public string Wgs84Wkt { get; set; }

        [JsonProperty("wgs84_projjson")]
        public string Wgs84ProjJson { get; set; }

        [JsonProperty("wgs84_control_points")]
        public List<TerrainWgs84ControlPoint> Wgs84ControlPoints { get; set; } =
            new List<TerrainWgs84ControlPoint>();

        public static string GetMapSidecarPath(string directory)
        {
            return Path.Combine(directory, MapSidecarFileName);
        }

        public static string GetRasterSidecarPath(string rasterPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(rasterPath));
            string stem = Path.GetFileNameWithoutExtension(rasterPath);
            return Path.Combine(
                directory,
                stem + ".terrainlab-georef.json");
        }

        public static bool TryReadMapSidecar(
            string directory,
            int expectedWidth,
            int expectedHeight,
            out TerrainRasterGeoreference georeference,
            out string error)
        {
            georeference = null;
            error = null;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string path = GetMapSidecarPath(directory);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaximumSidecarBytes)
                {
                    throw new InvalidDataException(
                        "TerrainLab georeference sidecar has an invalid size.");
                }

                TerrainRasterGeoreference parsed =
                    JsonConvert.DeserializeObject<TerrainRasterGeoreference>(
                        File.ReadAllText(path, Encoding.UTF8));
                parsed.Validate(expectedWidth, expectedHeight);
                georeference = parsed;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public void Validate(int expectedWidth, int expectedHeight)
        {
            if (!string.Equals(Format, FormatId, StringComparison.Ordinal) ||
                SchemaVersion?.Split('.')[0] != "1")
            {
                throw new InvalidDataException(
                    "Unsupported TerrainLab raster georeference schema.");
            }

            if (SourceWidth <= 0 || SourceHeight <= 0 ||
                RasterWidth != expectedWidth || RasterHeight != expectedHeight)
            {
                throw new InvalidDataException(
                    "TerrainLab georeference dimensions do not match the raster.");
            }

            ValidateTransform(SourceRasterToCrs, "source_raster_to_crs");
            ValidateTransform(RasterToCrs, "raster_to_crs");
            ValidateTransform(WorldBoxCellToCrs, "worldbox_cell_to_crs");
            ValidateTransform(WorldBoxMetreToCrs, "worldbox_metre_to_crs");
            if (CrsToWorldBoxCell == null)
            {
                CrsToWorldBoxCell = InvertTransform(WorldBoxCellToCrs);
            }

            if (CrsToWorldBoxMetre == null)
            {
                CrsToWorldBoxMetre = InvertTransform(WorldBoxMetreToCrs);
            }

            ValidateTransform(CrsToWorldBoxCell, "crs_to_worldbox_cell");
            ValidateTransform(CrsToWorldBoxMetre, "crs_to_worldbox_metre");
            ValidateDerivedTransform(
                WorldBoxCellToCrs,
                BuildWorldBoxTransform(1d),
                "worldbox_cell_to_crs");
            ValidateDerivedTransform(
                WorldBoxMetreToCrs,
                BuildWorldBoxTransform(WorldBoxMetresPerCell),
                "worldbox_metre_to_crs");
            ValidateDerivedTransform(
                CrsToWorldBoxCell,
                InvertTransform(WorldBoxCellToCrs),
                "crs_to_worldbox_cell");
            ValidateDerivedTransform(
                CrsToWorldBoxMetre,
                InvertTransform(WorldBoxMetreToCrs),
                "crs_to_worldbox_metre");
            if (!IsFinite(WorldBoxMetresPerCellValue) ||
                Math.Abs(WorldBoxMetresPerCellValue - WorldBoxMetresPerCell) >
                1e-9)
            {
                throw new InvalidDataException(
                    "TerrainLab georeference uses an unsupported local scale.");
            }

            if (Epsg.HasValue && (Epsg.Value <= 0 || Epsg.Value > 999999) ||
                VerticalEpsg.HasValue &&
                (VerticalEpsg.Value <= 0 || VerticalEpsg.Value > 999999) ||
                Wgs84Epsg != 4326)
            {
                throw new InvalidDataException(
                    "TerrainLab georeference contains an invalid EPSG code.");
            }

            if (!string.Equals(PixelInterpretation, "area", StringComparison.Ordinal) &&
                !string.Equals(PixelInterpretation, "point", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "TerrainLab pixel interpretation must be area or point.");
            }

            if ((GeoKeyDirectory?.Length ?? 0) > 4096 ||
                (GeoDoubleParams?.Length ?? 0) > 4096 ||
                (GeoAsciiParams?.Length ?? 0) > 65535 ||
                (CrsWkt?.Length ?? 0) > 512 * 1024 ||
                (CrsProjJson?.Length ?? 0) > 512 * 1024 ||
                (Wgs84ControlPoints?.Count ?? 0) > 256)
            {
                throw new InvalidDataException(
                    "TerrainLab georeference metadata exceeds safe limits.");
            }

            ValidateGeoKeys();
            foreach (double value in GeoDoubleParams ?? Array.Empty<double>())
            {
                if (!IsFinite(value))
                {
                    throw new InvalidDataException(
                        "GeoTIFF double parameters contain a non-finite value.");
                }
            }

            foreach (TerrainWgs84ControlPoint point in
                     Wgs84ControlPoints ?? new List<TerrainWgs84ControlPoint>())
            {
                if (point == null ||
                    !IsFinite(point.LocalX) || !IsFinite(point.LocalY) ||
                    !IsFinite(point.SourceX) || !IsFinite(point.SourceY) ||
                    !IsFinite(point.Longitude) || !IsFinite(point.Latitude) ||
                    point.LocalX < 0d || point.LocalX > RasterWidth ||
                    point.LocalY < 0d || point.LocalY > RasterHeight ||
                    point.Longitude < -180.000001d ||
                    point.Longitude > 180.000001d ||
                    point.Latitude < -90.000001d ||
                    point.Latitude > 90.000001d)
                {
                    throw new InvalidDataException(
                        "TerrainLab WGS84 control point is invalid.");
                }
            }
        }

        public ushort[] GetGeoKeyDirectory()
        {
            if (GeoKeyDirectory?.Length >= 4)
            {
                return (ushort[])GeoKeyDirectory.Clone();
            }

            ushort rasterType = string.Equals(
                PixelInterpretation,
                "point",
                StringComparison.Ordinal)
                ? (ushort)2
                : (ushort)1;
            if (!Epsg.HasValue)
            {
                return new ushort[]
                {
                    1, 1, 0, 2,
                    1024, 0, 1, 32767,
                    1025, 0, 1, rasterType
                };
            }

            bool projected =
                string.Equals(CrsKind, "projected", StringComparison.Ordinal) ||
                string.Equals(
                    CrsKind,
                    "compound_projected",
                    StringComparison.Ordinal);
            List<Tuple<ushort, ushort>> keys =
                new List<Tuple<ushort, ushort>>
                {
                    Tuple.Create(
                        (ushort)1024,
                        projected ? (ushort)1 : (ushort)2),
                    Tuple.Create((ushort)1025, rasterType),
                    Tuple.Create(
                        projected ? (ushort)3072 : (ushort)2048,
                        Epsg.Value <= ushort.MaxValue
                            ? checked((ushort)Epsg.Value)
                            : (ushort)32767)
                };
            if (VerticalEpsg.HasValue && VerticalEpsg.Value <= ushort.MaxValue)
            {
                keys.Add(Tuple.Create(
                    (ushort)4096,
                    checked((ushort)VerticalEpsg.Value)));
            }

            keys.Sort((left, right) => left.Item1.CompareTo(right.Item1));
            List<ushort> flattened = new List<ushort>
            {
                1,
                1,
                0,
                checked((ushort)keys.Count)
            };
            foreach (Tuple<ushort, ushort> key in keys)
            {
                flattened.Add(key.Item1);
                flattened.Add(0);
                flattened.Add(1);
                flattened.Add(key.Item2);
            }

            return flattened.ToArray();
        }

        public double[] GetModelTransformation()
        {
            ValidateTransform(RasterToCrs, "raster_to_crs");
            double[] value = RasterToCrs;
            double originX = value[0];
            double originY = value[3];
            if (string.Equals(
                PixelInterpretation,
                "point",
                StringComparison.Ordinal))
            {
                originX += 0.5d * value[1] + 0.5d * value[2];
                originY += 0.5d * value[4] + 0.5d * value[5];
            }

            return new[]
            {
                value[1], value[2], 0d, originX,
                value[4], value[5], 0d, originY,
                0d, 0d, 1d, 0d,
                0d, 0d, 0d, 1d
            };
        }

        public double[] GetWorldFile()
        {
            ValidateTransform(RasterToCrs, "raster_to_crs");
            double[] value = RasterToCrs;
            return new[]
            {
                value[1],
                value[4],
                value[2],
                value[5],
                value[0] + 0.5d * value[1] + 0.5d * value[2],
                value[3] + 0.5d * value[4] + 0.5d * value[5]
            };
        }

        public void WriteRasterSidecar(string rasterPath)
        {
            string path = GetRasterSidecarPath(rasterPath);
            File.WriteAllText(
                path,
                JsonConvert.SerializeObject(this, Formatting.Indented) +
                Environment.NewLine,
                new UTF8Encoding(false));
        }

        private double[] BuildWorldBoxTransform(double localUnitsPerCell)
        {
            double[] value = RasterToCrs;
            return new[]
            {
                value[0] + value[2] * RasterHeight,
                value[1] / localUnitsPerCell,
                -value[2] / localUnitsPerCell,
                value[3] + value[5] * RasterHeight,
                value[4] / localUnitsPerCell,
                -value[5] / localUnitsPerCell
            };
        }

        private void ValidateGeoKeys()
        {
            if (GeoKeyDirectory == null || GeoKeyDirectory.Length == 0)
            {
                return;
            }

            if (GeoKeyDirectory.Length < 4 ||
                GeoKeyDirectory[0] != 1 ||
                GeoKeyDirectory.Length !=
                4 + checked(GeoKeyDirectory[3] * 4))
            {
                throw new InvalidDataException(
                    "TerrainLab GeoKey directory is malformed.");
            }

            for (int index = 0; index < GeoKeyDirectory[3]; index++)
            {
                int offset = 4 + index * 4;
                ushort location = GeoKeyDirectory[offset + 1];
                int count = GeoKeyDirectory[offset + 2];
                int valueOffset = GeoKeyDirectory[offset + 3];
                if (count <= 0)
                {
                    throw new InvalidDataException(
                        "TerrainLab GeoKey has an invalid value count.");
                }

                if (location == 0 && count == 1)
                {
                    continue;
                }

                int available;
                if (location == 34735)
                {
                    available = GeoKeyDirectory.Length;
                }
                else if (location == 34736)
                {
                    available = GeoDoubleParams?.Length ?? 0;
                }
                else if (location == 34737)
                {
                    available = (GeoAsciiParams?.Length ?? 0) + 1;
                }
                else
                {
                    throw new InvalidDataException(
                        "TerrainLab GeoKey references an unsupported TIFF tag.");
                }

                if (valueOffset < 0 || valueOffset > available ||
                    count > available - valueOffset)
                {
                    throw new InvalidDataException(
                        "TerrainLab GeoKey references data outside its parameter tag.");
                }
            }
        }

        private static void ValidateDerivedTransform(
            double[] actual,
            double[] expected,
            string name)
        {
            for (int index = 0; index < 6; index++)
            {
                double tolerance = Math.Max(
                    1e-10,
                    Math.Abs(expected[index]) * 1e-10);
                if (Math.Abs(actual[index] - expected[index]) > tolerance)
                {
                    throw new InvalidDataException(
                        "TerrainLab " + name +
                        " is inconsistent with raster_to_crs.");
                }
            }
        }

        private static double[] InvertTransform(double[] value)
        {
            ValidateTransform(value, "affine_transform");
            double determinant =
                value[1] * value[5] - value[2] * value[4];
            return new[]
            {
                (-value[5] * value[0] + value[2] * value[3]) /
                    determinant,
                value[5] / determinant,
                -value[2] / determinant,
                (value[4] * value[0] - value[1] * value[3]) /
                    determinant,
                -value[4] / determinant,
                value[1] / determinant
            };
        }

        private static void ValidateTransform(double[] value, string name)
        {
            if (value == null || value.Length != 6 ||
                value.Any(item => !IsFinite(item)))
            {
                throw new InvalidDataException(
                    "TerrainLab " + name + " must contain six finite coefficients.");
            }

            double determinant = value[1] * value[5] - value[2] * value[4];
            if (Math.Abs(determinant) <= 1e-18)
            {
                throw new InvalidDataException(
                    "TerrainLab " + name + " is not invertible.");
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static string FormatNumber(double value)
        {
            return value.ToString("0.###############", CultureInfo.InvariantCulture);
        }
    }
}
