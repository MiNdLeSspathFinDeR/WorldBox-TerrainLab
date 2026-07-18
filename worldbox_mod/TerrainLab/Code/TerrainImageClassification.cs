using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace TerrainLab
{
    public sealed class TerrainImageClassOption
    {
        public TerrainImageClassOption(
            string id,
            string localizationKey,
            short defaultElevation)
        {
            Id = id;
            LocalizationKey = localizationKey;
            DefaultElevation = defaultElevation;
        }

        public string Id { get; }

        public string LocalizationKey { get; }

        public short DefaultElevation { get; }
    }

    public sealed class TerrainImageBiotopeOption
    {
        public TerrainImageBiotopeOption(string id, string localizationKey)
        {
            Id = id;
            LocalizationKey = localizationKey;
        }

        public string Id { get; }

        public string LocalizationKey { get; }
    }

    public static class TerrainImageClassificationCatalog
    {
        public static readonly IReadOnlyList<TerrainImageClassOption> Surfaces =
            new[]
            {
                new TerrainImageClassOption(
                    "deep_ocean",
                    "terrain_lab_manual_surface_deep_ocean",
                    -4000),
                new TerrainImageClassOption(
                    "shelf",
                    "terrain_lab_manual_surface_shelf",
                    -80),
                new TerrainImageClassOption(
                    "shallow_water",
                    "terrain_lab_manual_surface_shallow_water",
                    -3),
                new TerrainImageClassOption(
                    "river_lake",
                    "terrain_lab_manual_surface_river_lake",
                    100),
                new TerrainImageClassOption(
                    "sand",
                    "terrain_lab_manual_surface_sand",
                    2),
                new TerrainImageClassOption(
                    "plain",
                    "terrain_lab_manual_surface_plain",
                    150),
                new TerrainImageClassOption(
                    "lowland",
                    "terrain_lab_manual_surface_lowland",
                    50),
                new TerrainImageClassOption(
                    "upland",
                    "terrain_lab_manual_surface_upland",
                    800),
                new TerrainImageClassOption(
                    "hills",
                    "terrain_lab_manual_surface_hills",
                    1500),
                new TerrainImageClassOption(
                    "rocks",
                    "terrain_lab_manual_surface_rocks",
                    3000),
                new TerrainImageClassOption(
                    "summit",
                    "terrain_lab_manual_surface_summit",
                    5000),
                new TerrainImageClassOption(
                    "depression",
                    "terrain_lab_manual_surface_depression",
                    -50)
            };

        public static readonly IReadOnlyList<TerrainImageBiotopeOption> Biotopes =
            new[]
            {
                new TerrainImageBiotopeOption(
                    "auto",
                    "terrain_lab_manual_biotope_auto"),
                new TerrainImageBiotopeOption(
                    "none",
                    "terrain_lab_manual_biotope_none"),
                new TerrainImageBiotopeOption(
                    "grass",
                    "terrain_lab_manual_biotope_grass"),
                new TerrainImageBiotopeOption(
                    "jungle",
                    "terrain_lab_manual_biotope_jungle"),
                new TerrainImageBiotopeOption(
                    "savanna",
                    "terrain_lab_manual_biotope_savanna"),
                new TerrainImageBiotopeOption(
                    "desert",
                    "terrain_lab_manual_biotope_desert"),
                new TerrainImageBiotopeOption(
                    "permafrost",
                    "terrain_lab_manual_biotope_permafrost"),
                new TerrainImageBiotopeOption(
                    "swamp",
                    "terrain_lab_manual_biotope_swamp"),
                new TerrainImageBiotopeOption(
                    "enchanted",
                    "terrain_lab_manual_biotope_enchanted")
            };

        public static TerrainImageClassOption FindSurface(string id)
        {
            return Surfaces.FirstOrDefault(option =>
                string.Equals(option.Id, id, StringComparison.Ordinal));
        }

        public static TerrainImageBiotopeOption FindBiotope(string id)
        {
            return Biotopes.FirstOrDefault(option =>
                string.Equals(option.Id, id, StringComparison.Ordinal));
        }
    }

    public sealed class TerrainImageClassificationSource
    {
        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }
    }

    public sealed class TerrainImageClassificationSettings
    {
        [JsonProperty("color_weight")]
        public double ColorWeight { get; set; } = 0.55;

        [JsonProperty("texture_weight")]
        public double TextureWeight { get; set; } = 0.20;

        [JsonProperty("spatial_weight")]
        public double SpatialWeight { get; set; } = 0.25;

        [JsonProperty("appearance_tolerance")]
        public double AppearanceTolerance { get; set; } = 0.65;

        [JsonProperty("local_influence")]
        public double LocalInfluence { get; set; } = 0.08;

        [JsonProperty("elevation_power")]
        public double ElevationPower { get; set; } = 2.0;

        [JsonProperty("elevation_smoothing")]
        public int ElevationSmoothing { get; set; } = 1;

        [JsonProperty("interpolate_elevation_globally")]
        public bool InterpolateElevationGlobally { get; set; } = true;
    }

    public sealed class TerrainImageClassificationSample
    {
        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }

        [JsonProperty("surface")]
        public string Surface { get; set; }

        [JsonProperty("biotope")]
        public string Biotope { get; set; }

        [JsonProperty("elevation")]
        public short Elevation { get; set; }
    }

    public sealed class TerrainImageClassificationVertex
    {
        public TerrainImageClassificationVertex()
        {
        }

        public TerrainImageClassificationVertex(int x, int y)
        {
            X = x;
            Y = y;
        }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("y")]
        public int Y { get; set; }
    }

    public sealed class TerrainImageClassificationRegion
    {
        [JsonProperty("vertices")]
        public List<TerrainImageClassificationVertex> Vertices { get; set; } =
            new List<TerrainImageClassificationVertex>();

        [JsonProperty("surface")]
        public string Surface { get; set; }

        [JsonProperty("biotope")]
        public string Biotope { get; set; }

        [JsonProperty("elevation")]
        public short Elevation { get; set; }
    }

    public sealed class TerrainImageMapBoundary
    {
        [JsonProperty("vertices")]
        public List<TerrainImageClassificationVertex> Vertices { get; set; } =
            new List<TerrainImageClassificationVertex>();

        [JsonProperty("outside_surface")]
        public string OutsideSurface { get; set; } = "deep_ocean";

        [JsonProperty("outside_elevation")]
        public short OutsideElevation { get; set; } = -4000;
    }

    public sealed class TerrainImageClassificationProfile
    {
        public const int CurrentSchemaVersion = 1;
        public const int MaximumSamples = 512;
        public const int MaximumRegions = 128;
        public const int MaximumRegionVertices = 256;
        public const int MaximumTotalRegionVertices = 8192;
        public const int MaximumProfileBytes = 4 * 1024 * 1024;
        public const string SidecarSuffix =
            ".terrainlab-classification.json";

        [JsonProperty("schema_version")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("source")]
        public TerrainImageClassificationSource Source { get; set; }

        [JsonProperty("settings")]
        public TerrainImageClassificationSettings Settings { get; set; } =
            new TerrainImageClassificationSettings();

        [JsonProperty("samples")]
        public List<TerrainImageClassificationSample> Samples { get; set; } =
            new List<TerrainImageClassificationSample>();

        [JsonProperty("regions")]
        public List<TerrainImageClassificationRegion> Regions { get; set; } =
            new List<TerrainImageClassificationRegion>();

        [JsonProperty(
            "map_boundary",
            NullValueHandling = NullValueHandling.Ignore)]
        public TerrainImageMapBoundary MapBoundary { get; set; }

        [JsonProperty("updated_utc")]
        public string UpdatedUtc { get; set; }

        [JsonIgnore]
        public bool HasUsableTraining =>
            (Samples?.Count ?? 0) >= 2 ||
            (Regions?.Count ?? 0) >= 1;

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

        public static TerrainImageClassificationProfile Create(
            string imagePath,
            int width,
            int height)
        {
            TerrainImageClassificationProfile profile =
                new TerrainImageClassificationProfile
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

        public static TerrainImageClassificationProfile LoadOrCreate(
            string imagePath,
            int width,
            int height)
        {
            string profilePath = GetSidecarPath(imagePath);
            if (!File.Exists(profilePath))
            {
                return Create(imagePath, width, height);
            }

            FileInfo info = new FileInfo(profilePath);
            if (info.Length > MaximumProfileBytes)
            {
                throw new InvalidDataException(
                    "Manual classification profile exceeds 4 MiB.");
            }

            TerrainImageClassificationProfile profile =
                JsonConvert.DeserializeObject<TerrainImageClassificationProfile>(
                    File.ReadAllText(profilePath),
                    new JsonSerializerSettings
                    {
                        MaxDepth = 32,
                        MissingMemberHandling = MissingMemberHandling.Ignore
                    });
            if (profile == null)
            {
                throw new InvalidDataException(
                    "Manual classification profile is empty.");
            }

            profile.Validate(width, height);
            return profile;
        }

        public void AddOrReplaceSample(
            int x,
            int y,
            string surface,
            string biotope,
            short elevation)
        {
            if (Source == null)
            {
                throw new InvalidOperationException(
                    "Classification profile has no source image.");
            }

            if (x < 0 || x >= Source.Width ||
                y < 0 || y >= Source.Height ||
                TerrainImageClassificationCatalog.FindSurface(surface) == null ||
                TerrainImageClassificationCatalog.FindBiotope(biotope) == null ||
                !TerrainElevationEncoding.IsDataValue(elevation) ||
                elevation == TerrainElevationEncoding.NoData)
            {
                throw new InvalidDataException(
                    "Manual classification sample is outside supported ranges.");
            }

            TerrainImageClassificationSample replacement =
                new TerrainImageClassificationSample
                {
                    X = x,
                    Y = y,
                    Surface = surface,
                    Biotope = biotope,
                    Elevation = elevation
                };
            int existing = Samples.FindIndex(sample =>
                sample.X == x && sample.Y == y);
            if (existing >= 0)
            {
                Samples[existing] = replacement;
            }
            else
            {
                if (Samples.Count >= MaximumSamples)
                {
                    throw new InvalidOperationException(
                        "Manual classification is limited to 512 samples.");
                }

                Samples.Add(replacement);
            }

            Validate(Source.Width, Source.Height);
        }

        public void AddRegion(
            IEnumerable<TerrainImageClassificationVertex> vertices,
            string surface,
            string biotope,
            short elevation)
        {
            if (Source == null)
            {
                throw new InvalidOperationException(
                    "Classification profile has no source image.");
            }
            if (TerrainImageClassificationCatalog.FindSurface(surface) == null ||
                TerrainImageClassificationCatalog.FindBiotope(biotope) == null ||
                !TerrainElevationEncoding.IsDataValue(elevation) ||
                elevation == TerrainElevationEncoding.NoData)
            {
                throw new InvalidDataException(
                    "Manual classification region has invalid class values.");
            }
            if (Regions == null)
            {
                Regions = new List<TerrainImageClassificationRegion>();
            }
            if (Regions.Count >= MaximumRegions)
            {
                throw new InvalidOperationException(
                    "Manual classification is limited to 128 regions.");
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
                copied[0].X == copied[copied.Count - 1].X &&
                copied[0].Y == copied[copied.Count - 1].Y)
            {
                copied.RemoveAt(copied.Count - 1);
            }
            ValidateRegionVertices(
                copied,
                Source.Width,
                Source.Height);
            int totalVertices = Regions.Sum(region =>
                region?.Vertices?.Count ?? 0);
            if (totalVertices + copied.Count > MaximumTotalRegionVertices)
            {
                throw new InvalidOperationException(
                    "Manual classification is limited to 8192 polygon vertices.");
            }

            Regions.Add(
                new TerrainImageClassificationRegion
                {
                    Vertices = copied,
                    Surface = surface,
                    Biotope = biotope,
                    Elevation = elevation
                });
            Validate(Source.Width, Source.Height);
        }

        public void SetMapBoundary(
            IEnumerable<TerrainImageClassificationVertex> vertices)
        {
            if (Source == null)
            {
                throw new InvalidOperationException(
                    "Classification profile has no source image.");
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
                copied[0].X == copied[copied.Count - 1].X &&
                copied[0].Y == copied[copied.Count - 1].Y)
            {
                copied.RemoveAt(copied.Count - 1);
            }
            ValidateRegionVertices(
                copied,
                Source.Width,
                Source.Height);
            MapBoundary = new TerrainImageMapBoundary
            {
                Vertices = copied,
                OutsideSurface = "deep_ocean",
                OutsideElevation = -4000
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

        public bool IsInsideMapBoundary(int x, int y)
        {
            return MapBoundary == null ||
                   ContainsPoint(MapBoundary.Vertices, x, y);
        }

        public bool RemoveLastAnnotation()
        {
            if (Regions != null && Regions.Count > 0)
            {
                Regions.RemoveAt(Regions.Count - 1);
                return true;
            }
            if (Samples == null || Samples.Count == 0)
            {
                return false;
            }

            Samples.RemoveAt(Samples.Count - 1);
            return true;
        }

        public bool RemoveRegionAt(int x, int y)
        {
            if (Regions == null)
            {
                return false;
            }
            for (int index = Regions.Count - 1; index >= 0; index--)
            {
                if (!ContainsPoint(Regions[index]?.Vertices, x, y))
                {
                    continue;
                }

                Regions.RemoveAt(index);
                return true;
            }
            return false;
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
            if (SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Unsupported manual classification schema version.");
            }

            if (Source == null || Source.Width <= 0 || Source.Height <= 0 ||
                Source.Width != expectedWidth || Source.Height != expectedHeight)
            {
                throw new InvalidDataException(
                    "Manual classification dimensions do not match the image.");
            }

            if (Settings == null)
            {
                Settings = new TerrainImageClassificationSettings();
            }

            if (!IsFiniteRange(Settings.ColorWeight, 0.0, 10.0) ||
                !IsFiniteRange(Settings.TextureWeight, 0.0, 10.0) ||
                !IsFiniteRange(Settings.SpatialWeight, 0.0, 10.0) ||
                Settings.ColorWeight + Settings.TextureWeight +
                Settings.SpatialWeight <= 0.0 ||
                !IsFiniteRange(Settings.AppearanceTolerance, 0.01, 8.0) ||
                !IsFiniteRange(Settings.LocalInfluence, 0.001, 1.0) ||
                !IsFiniteRange(Settings.ElevationPower, 0.25, 8.0) ||
                Settings.ElevationSmoothing < 0 ||
                Settings.ElevationSmoothing > 8)
            {
                throw new InvalidDataException(
                    "Manual classification settings are outside supported ranges.");
            }

            if (Samples == null)
            {
                Samples = new List<TerrainImageClassificationSample>();
            }

            if (Samples.Count > MaximumSamples)
            {
                throw new InvalidDataException(
                    "Manual classification exceeds 512 samples.");
            }

            HashSet<long> coordinates = new HashSet<long>();
            foreach (TerrainImageClassificationSample sample in Samples)
            {
                if (sample == null ||
                    sample.X < 0 || sample.X >= Source.Width ||
                    sample.Y < 0 || sample.Y >= Source.Height ||
                    TerrainImageClassificationCatalog.FindSurface(
                        sample.Surface) == null ||
                    TerrainImageClassificationCatalog.FindBiotope(
                        sample.Biotope) == null ||
                    !TerrainElevationEncoding.IsDataValue(sample.Elevation) ||
                    sample.Elevation == TerrainElevationEncoding.NoData)
                {
                    throw new InvalidDataException(
                        "Manual classification contains an invalid sample.");
                }

                long coordinate = ((long)sample.Y << 32) | (uint)sample.X;
                if (!coordinates.Add(coordinate))
                {
                    throw new InvalidDataException(
                        "Manual classification contains duplicate sample coordinates.");
                }
            }

            if (Regions == null)
            {
                Regions = new List<TerrainImageClassificationRegion>();
            }
            if (Regions.Count > MaximumRegions)
            {
                throw new InvalidDataException(
                    "Manual classification exceeds 128 regions.");
            }

            int totalRegionVertices = 0;
            foreach (TerrainImageClassificationRegion region in Regions)
            {
                if (region == null ||
                    TerrainImageClassificationCatalog.FindSurface(
                        region.Surface) == null ||
                    TerrainImageClassificationCatalog.FindBiotope(
                        region.Biotope) == null ||
                    !TerrainElevationEncoding.IsDataValue(region.Elevation) ||
                    region.Elevation == TerrainElevationEncoding.NoData)
                {
                    throw new InvalidDataException(
                        "Manual classification contains an invalid region.");
                }

                if (region.Vertices?.Count >= 4)
                {
                    TerrainImageClassificationVertex first =
                        region.Vertices[0];
                    TerrainImageClassificationVertex last =
                        region.Vertices[region.Vertices.Count - 1];
                    if (first != null &&
                        last != null &&
                        first.X == last.X &&
                        first.Y == last.Y)
                    {
                        region.Vertices.RemoveAt(region.Vertices.Count - 1);
                    }
                }
                ValidateRegionVertices(
                    region.Vertices,
                    Source.Width,
                    Source.Height);
                totalRegionVertices += region.Vertices.Count;
                if (totalRegionVertices > MaximumTotalRegionVertices)
                {
                    throw new InvalidDataException(
                        "Manual classification exceeds 8192 polygon vertices.");
                }
            }

            if (MapBoundary != null)
            {
                if (MapBoundary.Vertices?.Count >= 4)
                {
                    TerrainImageClassificationVertex first =
                        MapBoundary.Vertices[0];
                    TerrainImageClassificationVertex last =
                        MapBoundary.Vertices[
                            MapBoundary.Vertices.Count - 1];
                    if (first != null &&
                        last != null &&
                        first.X == last.X &&
                        first.Y == last.Y)
                    {
                        MapBoundary.Vertices.RemoveAt(
                            MapBoundary.Vertices.Count - 1);
                    }
                }
                ValidateRegionVertices(
                    MapBoundary.Vertices,
                    Source.Width,
                    Source.Height);
                if (!string.Equals(
                        MapBoundary.OutsideSurface,
                        "deep_ocean",
                        StringComparison.Ordinal) ||
                    !TerrainElevationEncoding.IsDataValue(
                        MapBoundary.OutsideElevation) ||
                    MapBoundary.OutsideElevation > -151)
                {
                    throw new InvalidDataException(
                        "Map boundary outside area must be deep ocean at -20000..-151 m.");
                }
            }
        }

        private static void ValidateRegionVertices(
            IReadOnlyList<TerrainImageClassificationVertex> vertices,
            int width,
            int height)
        {
            if (vertices == null ||
                vertices.Count < 3 ||
                vertices.Count > MaximumRegionVertices)
            {
                throw new InvalidDataException(
                    "A classification region needs 3..256 vertices.");
            }

            HashSet<long> coordinates = new HashSet<long>();
            for (int index = 0; index < vertices.Count; index++)
            {
                TerrainImageClassificationVertex vertex = vertices[index];
                if (vertex == null ||
                    vertex.X < 0 || vertex.X >= width ||
                    vertex.Y < 0 || vertex.Y >= height)
                {
                    throw new InvalidDataException(
                        "A classification region vertex is outside the image.");
                }
                long coordinate = ((long)vertex.Y << 32) | (uint)vertex.X;
                if (!coordinates.Add(coordinate))
                {
                    throw new InvalidDataException(
                        "A classification region contains duplicate vertices.");
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
                    if (firstNext == second || secondNext == first)
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
                            "A classification region is self-intersecting.");
                    }
                }
            }
            if (SignedAreaTwice(vertices) == 0)
            {
                throw new InvalidDataException(
                    "A classification region has zero area.");
            }
        }

        private static bool ContainsPoint(
            IReadOnlyList<TerrainImageClassificationVertex> vertices,
            int x,
            int y)
        {
            if (vertices == null || vertices.Count < 3)
            {
                return false;
            }

            bool inside = false;
            TerrainImageClassificationVertex previous =
                vertices[vertices.Count - 1];
            for (int index = 0; index < vertices.Count; index++)
            {
                TerrainImageClassificationVertex current = vertices[index];
                if (Orientation(previous, current, x, y) == 0 &&
                    x >= Math.Min(previous.X, current.X) &&
                    x <= Math.Max(previous.X, current.X) &&
                    y >= Math.Min(previous.Y, current.Y) &&
                    y <= Math.Max(previous.Y, current.Y))
                {
                    return true;
                }

                if ((current.Y > y) != (previous.Y > y))
                {
                    double intersectionX =
                        current.X +
                        (double)(previous.X - current.X) *
                        (y - current.Y) /
                        (previous.Y - current.Y);
                    if (x < intersectionX)
                    {
                        inside = !inside;
                    }
                }
                previous = current;
            }
            return inside;
        }

        private static long SignedAreaTwice(
            IReadOnlyList<TerrainImageClassificationVertex> vertices)
        {
            long area = 0;
            TerrainImageClassificationVertex previous =
                vertices[vertices.Count - 1];
            foreach (TerrainImageClassificationVertex current in vertices)
            {
                area += (long)previous.X * current.Y -
                        (long)current.X * previous.Y;
                previous = current;
            }
            return area;
        }

        private static bool SegmentsIntersect(
            TerrainImageClassificationVertex firstStart,
            TerrainImageClassificationVertex firstEnd,
            TerrainImageClassificationVertex secondStart,
            TerrainImageClassificationVertex secondEnd)
        {
            long first = Orientation(
                firstStart,
                firstEnd,
                secondStart.X,
                secondStart.Y);
            long second = Orientation(
                firstStart,
                firstEnd,
                secondEnd.X,
                secondEnd.Y);
            long third = Orientation(
                secondStart,
                secondEnd,
                firstStart.X,
                firstStart.Y);
            long fourth = Orientation(
                secondStart,
                secondEnd,
                firstEnd.X,
                firstEnd.Y);
            if (first == 0 && PointOnSegment(
                    secondStart,
                    firstStart,
                    firstEnd) ||
                second == 0 && PointOnSegment(
                    secondEnd,
                    firstStart,
                    firstEnd) ||
                third == 0 && PointOnSegment(
                    firstStart,
                    secondStart,
                    secondEnd) ||
                fourth == 0 && PointOnSegment(
                    firstEnd,
                    secondStart,
                    secondEnd))
            {
                return true;
            }
            return (first > 0) != (second > 0) &&
                   (third > 0) != (fourth > 0);
        }

        private static long Orientation(
            TerrainImageClassificationVertex start,
            TerrainImageClassificationVertex end,
            int x,
            int y)
        {
            return (long)(end.X - start.X) * (y - start.Y) -
                   (long)(end.Y - start.Y) * (x - start.X);
        }

        private static bool PointOnSegment(
            TerrainImageClassificationVertex point,
            TerrainImageClassificationVertex start,
            TerrainImageClassificationVertex end)
        {
            return point.X >= Math.Min(start.X, end.X) &&
                   point.X <= Math.Max(start.X, end.X) &&
                   point.Y >= Math.Min(start.Y, end.Y) &&
                   point.Y <= Math.Max(start.Y, end.Y);
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
