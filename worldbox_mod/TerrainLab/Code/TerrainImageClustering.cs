using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TerrainLab
{
    public sealed class TerrainImageClusteringSettings
    {
        [JsonProperty("long_side_blocks")]
        public int LongSideBlocks { get; set; } = 20;

        [JsonProperty("clusters")]
        public int Clusters { get; set; } = 14;

        [JsonProperty("spline_radius")]
        public int SplineRadius { get; set; }

        [JsonProperty("smooth_passes")]
        public int SmoothPasses { get; set; } = 1;

        [JsonProperty("min_land_region")]
        public int MinimumLandRegion { get; set; } = 32;

        [JsonProperty("water_sensitivity")]
        public double WaterSensitivity { get; set; } = 1.0;

        [JsonProperty("color_weight")]
        public double ColorWeight { get; set; } = 1.0;

        [JsonProperty("luma_weight")]
        public double LumaWeight { get; set; } = 1.0;

        [JsonProperty("saturation_weight")]
        public double SaturationWeight { get; set; } = 1.0;

        [JsonProperty("texture_weight")]
        public double TextureWeight { get; set; }

        [JsonProperty("slope_weight")]
        public double SlopeWeight { get; set; } = 1.0;

        [JsonProperty("spatial_weight")]
        public double SpatialWeight { get; set; }

        [JsonProperty("detail_weight")]
        public double DetailWeight { get; set; } = 0.65;

        [JsonProperty("sample_limit")]
        public int SampleLimit { get; set; } = 60000;

        [JsonProperty("kmeans_iterations")]
        public int KMeansIterations { get; set; } = 18;

        [JsonProperty("random_seed")]
        public int RandomSeed { get; set; } = 1729;
    }

    public sealed class TerrainImageClusteringBoundary
    {
        [JsonProperty("vertices")]
        public List<TerrainImageClassificationVertex> Vertices { get; set; } =
            new List<TerrainImageClassificationVertex>();
    }

    public sealed class TerrainImageClusteringComposition
    {
        [JsonProperty(
            "allowed_surfaces",
            ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AllowedSurfaces { get; set; } =
            TerrainImageClassificationCatalog.Surfaces
                .Select(option => option.Id)
                .ToList();

        [JsonProperty(
            "allowed_biotopes",
            ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> AllowedBiotopes { get; set; } =
            TerrainImageClassificationCatalog.SelectableBiotopes
                .Select(option => option.Id)
                .ToList();
    }

    public sealed class TerrainImageClusteringProfile
    {
        public const int CurrentSchemaVersion = 3;
        public const int MaximumBoundaryVertices = 256;
        public const int MaximumProfileBytes = 1024 * 1024;
        public const string SidecarSuffix = ".terrainlab-clustering.json";

        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("source")]
        public TerrainImageClassificationSource Source { get; set; }

        [JsonProperty("settings")]
        public TerrainImageClusteringSettings Settings { get; set; } =
            new TerrainImageClusteringSettings();

        [JsonProperty("composition")]
        public TerrainImageClusteringComposition Composition { get; set; } =
            new TerrainImageClusteringComposition();

        [JsonProperty(
            "map_boundary",
            NullValueHandling = NullValueHandling.Ignore)]
        public TerrainImageClusteringBoundary MapBoundary { get; set; }

        [JsonProperty("updated_utc")]
        public string UpdatedUtc { get; set; }

        public static string GetSidecarPath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException(
                    "Source image path is required.",
                    nameof(imagePath));
            }

            return Path.GetFullPath(imagePath) + SidecarSuffix;
        }

        public static TerrainImageClusteringProfile Create(
            string imagePath,
            int width,
            int height)
        {
            TerrainImageClusteringProfile profile =
                new TerrainImageClusteringProfile
                {
                    Source = new TerrainImageClassificationSource
                    {
                        FileName = Path.GetFileName(imagePath),
                        Width = width,
                        Height = height
                    }
                };
            profile.Validate(width, height);
            return profile;
        }

        public static TerrainImageClusteringProfile LoadOrCreate(
            string imagePath,
            int width,
            int height)
        {
            string path = GetSidecarPath(imagePath);
            if (!File.Exists(path))
            {
                return Create(imagePath, width, height);
            }

            FileInfo info = new FileInfo(path);
            if (info.Length > MaximumProfileBytes)
            {
                throw new InvalidDataException(
                    "Clustering profile exceeds 1 MiB.");
            }

            TerrainImageClusteringProfile profile =
                JsonConvert.DeserializeObject<TerrainImageClusteringProfile>(
                    File.ReadAllText(path));
            if (profile == null)
            {
                throw new InvalidDataException(
                    "Clustering profile is empty.");
            }

            profile.UpgradeSchema();
            profile.Validate(width, height);
            return profile;
        }

        public void SetMapBoundary(
            IEnumerable<TerrainImageClassificationVertex> vertices)
        {
            if (Source == null)
            {
                throw new InvalidOperationException(
                    "Clustering profile has no source image.");
            }

            List<TerrainImageClassificationVertex> copied =
                vertices?
                    .Select(vertex =>
                        vertex == null
                            ? null
                            : new TerrainImageClassificationVertex(
                                vertex.X,
                                vertex.Y))
                    .ToList() ??
                new List<TerrainImageClassificationVertex>();
            if (copied.Count >= 4 &&
                copied[0] != null &&
                copied[copied.Count - 1] != null &&
                copied[0].X == copied[copied.Count - 1].X &&
                copied[0].Y == copied[copied.Count - 1].Y)
            {
                copied.RemoveAt(copied.Count - 1);
            }

            ValidateBoundaryVertices(copied, Source.Width, Source.Height);
            MapBoundary = new TerrainImageClusteringBoundary
            {
                Vertices = copied
            };
            Validate(Source.Width, Source.Height);
        }

        public bool ClearMapBoundary()
        {
            if (MapBoundary == null)
            {
                return false;
            }

            MapBoundary = null;
            return true;
        }

        public void GetOutputAspectDimensions(
            out int width,
            out int height)
        {
            TerrainMapLimits.GetEffectiveAspectDimensions(
                Source?.Width ?? 0,
                Source?.Height ?? 0,
                MapBoundary?.Vertices,
                out width,
                out height);
        }

        public void Save(string imagePath)
        {
            Validate(Source?.Width ?? 0, Source?.Height ?? 0);
            UpdatedUtc = DateTime.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture);
            string path = GetSidecarPath(imagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporaryPath =
                path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonConvert.SerializeObject(this, Formatting.Indented));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, null);
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is UnauthorizedAccessException ||
                        exception is PlatformNotSupportedException)
                    {
                        File.Copy(temporaryPath, path, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public void Validate(int expectedWidth, int expectedHeight)
        {
            UpgradeSchema();
            if (SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Unsupported clustering profile schema version.");
            }
            if (Source == null || Source.Width <= 0 || Source.Height <= 0 ||
                Source.Width != expectedWidth || Source.Height != expectedHeight)
            {
                throw new InvalidDataException(
                    "Clustering profile dimensions do not match the image.");
            }

            if (Settings == null)
            {
                Settings = new TerrainImageClusteringSettings();
            }
            ValidateSettings(Settings);
            GetOutputAspectDimensions(
                out int outputAspectWidth,
                out int outputAspectHeight);
            if (!TerrainMapLimits.TryGetBlockDimensions(
                    outputAspectWidth,
                    outputAspectHeight,
                    Settings.LongSideBlocks,
                    out _,
                    out _))
            {
                throw new InvalidDataException(
                    "Clustering output size exceeds the map cell budget.");
            }
            ValidateComposition();

            if (MapBoundary == null)
            {
                return;
            }
            if (MapBoundary.Vertices?.Count >= 4)
            {
                TerrainImageClassificationVertex first =
                    MapBoundary.Vertices[0];
                TerrainImageClassificationVertex last =
                    MapBoundary.Vertices[MapBoundary.Vertices.Count - 1];
                if (first != null && last != null &&
                    first.X == last.X && first.Y == last.Y)
                {
                    MapBoundary.Vertices.RemoveAt(
                        MapBoundary.Vertices.Count - 1);
                }
            }
            ValidateBoundaryVertices(
                MapBoundary.Vertices,
                Source.Width,
                Source.Height);
        }

        private void UpgradeSchema()
        {
            if (SchemaVersion == CurrentSchemaVersion)
            {
                return;
            }
            if (SchemaVersion != 1 && SchemaVersion != 2)
            {
                return;
            }

            if (SchemaVersion == 1)
            {
                Composition = new TerrainImageClusteringComposition();
            }
            SchemaVersion = CurrentSchemaVersion;
        }

        private void ValidateComposition()
        {
            if (Composition == null)
            {
                Composition = new TerrainImageClusteringComposition();
            }
            if (Composition.AllowedSurfaces == null ||
                Composition.AllowedSurfaces.Count == 0 ||
                Composition.AllowedSurfaces.Any(id =>
                    TerrainImageClassificationCatalog.FindSurface(id) ==
                    null) ||
                Composition.AllowedSurfaces.Distinct(
                    StringComparer.Ordinal).Count() !=
                Composition.AllowedSurfaces.Count)
            {
                throw new InvalidDataException(
                    "Clustering composition has invalid surface classes.");
            }
            if (Composition.AllowedBiotopes == null ||
                Composition.AllowedBiotopes.Count == 0 ||
                Composition.AllowedBiotopes.Any(id =>
                    TerrainImageClassificationCatalog.FindBiotope(id) ==
                        null ||
                    string.Equals(id, "auto", StringComparison.Ordinal) ||
                    string.Equals(id, "none", StringComparison.Ordinal)) ||
                Composition.AllowedBiotopes.Distinct(
                    StringComparer.Ordinal).Count() !=
                Composition.AllowedBiotopes.Count)
            {
                throw new InvalidDataException(
                    "Clustering composition has invalid biotope classes.");
            }
        }

        private static void ValidateSettings(
            TerrainImageClusteringSettings settings)
        {
            if (settings.LongSideBlocks < 1 ||
                settings.LongSideBlocks > TerrainMapLimits.MaximumBlockCount ||
                settings.Clusters < 4 || settings.Clusters > 64 ||
                settings.SplineRadius < 0 || settings.SplineRadius > 12 ||
                settings.SmoothPasses < 0 || settings.SmoothPasses > 8 ||
                settings.MinimumLandRegion < 0 ||
                settings.MinimumLandRegion > 4096 ||
                !IsFiniteRange(settings.WaterSensitivity, 0.5, 2.0) ||
                !IsFiniteRange(settings.ColorWeight, 0.0, 3.0) ||
                !IsFiniteRange(settings.LumaWeight, 0.0, 3.0) ||
                !IsFiniteRange(settings.SaturationWeight, 0.0, 3.0) ||
                !IsFiniteRange(settings.TextureWeight, 0.0, 3.0) ||
                !IsFiniteRange(settings.SlopeWeight, 0.0, 3.0) ||
                !IsFiniteRange(settings.SpatialWeight, 0.0, 3.0) ||
                !IsFiniteRange(settings.DetailWeight, 0.0, 1.0) ||
                settings.SampleLimit < 1000 ||
                settings.SampleLimit > 250000 ||
                settings.KMeansIterations < 1 ||
                settings.KMeansIterations > 100 ||
                settings.RandomSeed < 0)
            {
                throw new InvalidDataException(
                    "Clustering settings are outside supported ranges.");
            }

            if (settings.ColorWeight + settings.LumaWeight +
                settings.SaturationWeight + settings.TextureWeight +
                settings.SlopeWeight + settings.SpatialWeight <= 0.0)
            {
                throw new InvalidDataException(
                    "At least one clustering feature weight must be positive.");
            }
        }

        private static void ValidateBoundaryVertices(
            IReadOnlyList<TerrainImageClassificationVertex> vertices,
            int width,
            int height)
        {
            if (vertices == null ||
                vertices.Count < 3 ||
                vertices.Count > MaximumBoundaryVertices)
            {
                throw new InvalidDataException(
                    "A clustering boundary needs 3..256 vertices.");
            }

            HashSet<long> coordinates = new HashSet<long>();
            foreach (TerrainImageClassificationVertex vertex in vertices)
            {
                if (vertex == null ||
                    vertex.X < 0 || vertex.X >= width ||
                    vertex.Y < 0 || vertex.Y >= height)
                {
                    throw new InvalidDataException(
                        "A clustering boundary vertex is outside the image.");
                }

                long coordinate = ((long)vertex.Y << 32) | (uint)vertex.X;
                if (!coordinates.Add(coordinate))
                {
                    throw new InvalidDataException(
                        "A clustering boundary contains duplicate vertices.");
                }
            }

            for (int first = 0; first < vertices.Count; first++)
            {
                int firstNext = (first + 1) % vertices.Count;
                for (int second = first + 1;
                     second < vertices.Count;
                     second++)
                {
                    int secondNext = (second + 1) % vertices.Count;
                    if (first == second || firstNext == second ||
                        secondNext == first)
                    {
                        continue;
                    }

                    if (SegmentsIntersect(
                            vertices[first],
                            vertices[firstNext],
                            vertices[second],
                            vertices[secondNext]))
                    {
                        throw new InvalidDataException(
                            "A clustering boundary self-intersects.");
                    }
                }
            }
        }

        private static bool SegmentsIntersect(
            TerrainImageClassificationVertex firstStart,
            TerrainImageClassificationVertex firstEnd,
            TerrainImageClassificationVertex secondStart,
            TerrainImageClassificationVertex secondEnd)
        {
            long firstOrientation =
                Orientation(firstStart, firstEnd, secondStart);
            long secondOrientation =
                Orientation(firstStart, firstEnd, secondEnd);
            long thirdOrientation =
                Orientation(secondStart, secondEnd, firstStart);
            long fourthOrientation =
                Orientation(secondStart, secondEnd, firstEnd);
            if (firstOrientation == 0 &&
                OnSegment(firstStart, secondStart, firstEnd))
            {
                return true;
            }
            if (secondOrientation == 0 &&
                OnSegment(firstStart, secondEnd, firstEnd))
            {
                return true;
            }
            if (thirdOrientation == 0 &&
                OnSegment(secondStart, firstStart, secondEnd))
            {
                return true;
            }
            if (fourthOrientation == 0 &&
                OnSegment(secondStart, firstEnd, secondEnd))
            {
                return true;
            }
            return (firstOrientation > 0) != (secondOrientation > 0) &&
                   (thirdOrientation > 0) != (fourthOrientation > 0);
        }

        private static long Orientation(
            TerrainImageClassificationVertex first,
            TerrainImageClassificationVertex second,
            TerrainImageClassificationVertex third)
        {
            return (long)(second.Y - first.Y) * (third.X - second.X) -
                   (long)(second.X - first.X) * (third.Y - second.Y);
        }

        private static bool OnSegment(
            TerrainImageClassificationVertex first,
            TerrainImageClassificationVertex middle,
            TerrainImageClassificationVertex last)
        {
            return middle.X >= Math.Min(first.X, last.X) &&
                   middle.X <= Math.Max(first.X, last.X) &&
                   middle.Y >= Math.Min(first.Y, last.Y) &&
                   middle.Y <= Math.Max(first.Y, last.Y);
        }

        private static bool IsFiniteRange(
            double value,
            double minimum,
            double maximum)
        {
            return !double.IsNaN(value) &&
                   !double.IsInfinity(value) &&
                   value >= minimum &&
                   value <= maximum;
        }
    }
}
