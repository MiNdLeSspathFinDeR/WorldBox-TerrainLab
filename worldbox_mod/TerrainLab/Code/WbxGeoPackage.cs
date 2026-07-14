using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TerrainLab
{
    public static class WbxGeoFormat
    {
        public const string Extension = ".wbxgeo";
        public const string SidecarFileName = "terrainlab" + Extension;
        public const string FormatId = "wbxgeo";
        public const string SchemaVersion = "1.0.0";
        public const string MimeType = "application/vnd.terrainlab.wbxgeo+zip";

        internal const string ManifestEntry = "manifest.json";
        internal const string BaseMapEntry = "base/map.wbox";
        internal const string BaseMetaEntry = "base/map.meta";
        internal const string BasePreviewEntry = "base/preview.png";
        internal const string ElevationEntry = "layers/elevation.i16";
        internal const string LandformEntry = "layers/landform.u8";
        internal const string MaterialEntry = "layers/material.u8";
    }

    public sealed class WbxGeoManifest
    {
        [JsonProperty("format")]
        public string Format { get; set; }

        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; }

        [JsonProperty("package_role")]
        public string PackageRole { get; set; }

        [JsonProperty("project_id")]
        public string ProjectId { get; set; }

        [JsonProperty("created_utc")]
        public string CreatedUtc { get; set; }

        [JsonProperty("modified_utc")]
        public string ModifiedUtc { get; set; }

        [JsonProperty("base_map")]
        public WbxGeoBaseMap BaseMap { get; set; }

        [JsonProperty("canvas")]
        public WbxGeoCanvas Canvas { get; set; }

        [JsonProperty("crs")]
        public WbxGeoCrs Crs { get; set; }

        [JsonProperty("vertical_reference")]
        public WbxGeoVerticalReference VerticalReference { get; set; }

        [JsonProperty("layers")]
        public List<WbxGeoLayerDescriptor> Layers { get; set; } =
            new List<WbxGeoLayerDescriptor>();

        [JsonProperty("modules")]
        public List<TerrainModuleDescriptor> Modules { get; set; } =
            new List<TerrainModuleDescriptor>();

        [JsonProperty("compatibility")]
        public WbxGeoCompatibility Compatibility { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    public sealed class WbxGeoBaseMap
    {
        [JsonProperty("entry")]
        public string Entry { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("worldbox_save_version")]
        public int WorldBoxSaveVersion { get; set; }
    }

    public sealed class WbxGeoCanvas
    {
        [JsonProperty("width_cells")]
        public int WidthCells { get; set; }

        [JsonProperty("height_cells")]
        public int HeightCells { get; set; }

        [JsonProperty("width_blocks")]
        public int WidthBlocks { get; set; }

        [JsonProperty("height_blocks")]
        public int HeightBlocks { get; set; }

        [JsonProperty("cell_count")]
        public long CellCount { get; set; }

        [JsonProperty("maximum_cell_count")]
        public long MaximumCellCount { get; set; }

        [JsonProperty("origin")]
        public string Origin { get; set; }

        [JsonProperty("row_order")]
        public string RowOrder { get; set; }

        [JsonProperty("cell_size")]
        public double CellSize { get; set; }
    }

    public sealed class WbxGeoCrs
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("horizontal_unit")]
        public string HorizontalUnit { get; set; }
    }

    public sealed class WbxGeoVerticalReference
    {
        [JsonProperty("datum")]
        public string Datum { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("sea_level")]
        public short SeaLevel { get; set; }

        [JsonProperty("storage_type")]
        public string StorageType { get; set; }

        [JsonProperty("nodata")]
        public short NoData { get; set; }
    }

    public sealed class WbxGeoLayerDescriptor
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("semantic")]
        public string Semantic { get; set; }

        [JsonProperty("entry")]
        public string Entry { get; set; }

        [JsonProperty("data_type")]
        public string DataType { get; set; }

        [JsonProperty("byte_order")]
        public string ByteOrder { get; set; }

        [JsonProperty("row_order")]
        public string RowOrder { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("module")]
        public string Module { get; set; }

        [JsonProperty("nodata", NullValueHandling = NullValueHandling.Ignore)]
        public int? NoData { get; set; }
    }

    public sealed class WbxGeoCompatibility
    {
        [JsonProperty("vanilla_fallback")]
        public bool VanillaFallback { get; set; }

        [JsonProperty("external_map_name")]
        public string ExternalMapName { get; set; }

        [JsonProperty("unknown_optional_modules")]
        public string UnknownOptionalModules { get; set; }
    }

    public static class WbxGeoPackage
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string GetSidecarPath(string directory)
        {
            return Path.Combine(NormalizeDirectory(directory), WbxGeoFormat.SidecarFileName);
        }

        public static void Save(
            string directory,
            TerrainWorldState state,
            SavedMap savedMap,
            TerrainModuleRegistry moduleRegistry)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (savedMap == null)
            {
                throw new ArgumentNullException(nameof(savedMap));
            }

            if (!TerrainMapLimits.TryValidate(state.Width, state.Height, out string limitError))
            {
                throw new InvalidOperationException(limitError);
            }

            if (state.SeaLevel == TerrainElevationEncoding.NoData)
            {
                throw new InvalidOperationException("Sea level may not use the reserved NODATA value.");
            }

            string normalizedDirectory = NormalizeDirectory(directory);
            string baseMapPath = Path.Combine(normalizedDirectory, "map.wbox");
            if (!File.Exists(baseMapPath))
            {
                throw new FileNotFoundException("Vanilla map.wbox was not written before WBXGEO.", baseMapPath);
            }

            Directory.CreateDirectory(normalizedDirectory);
            string destinationPath = GetSidecarPath(normalizedDirectory);
            string temporaryPath = destinationPath + ".tmp";

            byte[] elevationBytes = Int16ArrayToLittleEndianBytes(state.Elevation);
            byte[] landformBytes = state.Landform;
            byte[] materialBytes = state.Material;

            FileStream previousStream = null;
            ZipArchive previousArchive = null;
            WbxGeoManifest previousManifest = null;
            try
            {
                TryOpenPreviousPackage(
                    destinationPath,
                    out previousStream,
                    out previousArchive,
                    out previousManifest);

                WbxGeoManifest manifest = CreateManifest(
                    state,
                    savedMap,
                    baseMapPath,
                    elevationBytes,
                    landformBytes,
                    materialBytes);

                using (FileStream output = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None))
                using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    WriteTextEntry(
                        archive,
                        "mimetype",
                        WbxGeoFormat.MimeType,
                        CompressionLevel.NoCompression);

                    CopyFileToEntry(archive, baseMapPath, WbxGeoFormat.BaseMapEntry, false);
                    CopyOptionalFileToEntry(
                        archive,
                        Path.Combine(normalizedDirectory, "map.meta"),
                        WbxGeoFormat.BaseMetaEntry);
                    CopyOptionalFileToEntry(
                        archive,
                        Path.Combine(normalizedDirectory, "preview.png"),
                        WbxGeoFormat.BasePreviewEntry);

                    WriteBytesEntry(archive, WbxGeoFormat.ElevationEntry, elevationBytes, true);
                    WriteBytesEntry(archive, WbxGeoFormat.LandformEntry, landformBytes, true);
                    WriteBytesEntry(archive, WbxGeoFormat.MaterialEntry, materialBytes, true);

                    WriteRegisteredModules(archive, manifest, moduleRegistry, state);
                    PreserveUnknownModules(
                        previousArchive,
                        previousManifest,
                        archive,
                        manifest,
                        moduleRegistry);

                    string json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
                    WriteTextEntry(
                        archive,
                        WbxGeoFormat.ManifestEntry,
                        json,
                        CompressionLevel.Optimal);
                }
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
            }
            finally
            {
                previousArchive?.Dispose();
                previousStream?.Dispose();
            }

            ReplaceFile(temporaryPath, destinationPath);
        }

        public static bool TryLoad(
            string packagePath,
            string externalBaseMapPath,
            TerrainModuleRegistry moduleRegistry,
            out TerrainWorldState state,
            out string error)
        {
            state = null;
            error = null;

            try
            {
                using (FileStream stream = new FileStream(
                    packagePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    WbxGeoManifest manifest = ReadManifest(archive);
                    ValidateManifest(manifest);

                    if (!string.IsNullOrWhiteSpace(externalBaseMapPath) &&
                        File.Exists(externalBaseMapPath))
                    {
                        string externalHash = ComputeFileSha256(externalBaseMapPath);
                        if (!string.Equals(
                            externalHash,
                            manifest.BaseMap.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "WBXGEO does not match the external map.wbox checksum.");
                        }
                    }

                    int width = manifest.Canvas.WidthCells;
                    int height = manifest.Canvas.HeightCells;
                    int cellCount = checked(width * height);

                    WbxGeoLayerDescriptor elevationLayer = GetCoreLayer(
                        manifest,
                        "core.elevation",
                        "int16");
                    WbxGeoLayerDescriptor landformLayer = GetCoreLayer(
                        manifest,
                        "core.landform",
                        "uint8");
                    WbxGeoLayerDescriptor materialLayer = GetCoreLayer(
                        manifest,
                        "core.material",
                        "uint8");

                    if (elevationLayer.NoData != TerrainElevationEncoding.NoData)
                    {
                        throw new InvalidDataException("WBXGEO elevation NODATA must be 9999.");
                    }

                    byte[] elevationBytes = ReadCheckedEntry(
                        archive,
                        elevationLayer,
                        checked(cellCount * sizeof(short)));
                    byte[] landform = ReadCheckedEntry(archive, landformLayer, cellCount);
                    byte[] material = ReadCheckedEntry(archive, materialLayer, cellCount);
                    short[] elevation = LittleEndianBytesToInt16Array(elevationBytes);

                    DateTime.TryParse(
                        manifest.CreatedUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime createdUtc);

                    state = TerrainWorldState.CreateFromLayers(
                        manifest.ProjectId,
                        createdUtc,
                        width,
                        height,
                        manifest.VerticalReference?.SeaLevel ?? 0,
                        elevation,
                        landform,
                        material);

                    ReadRegisteredModules(archive, manifest, moduleRegistry, state);
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                state = null;
                return false;
            }
        }

        public static void ImportVanillaPayload(string packagePath, string targetDirectory)
        {
            string normalizedTarget = NormalizeDirectory(targetDirectory);
            Directory.CreateDirectory(normalizedTarget);

            using (FileStream stream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                WbxGeoManifest manifest = ReadManifest(archive);
                ValidateManifest(manifest);
                ValidateEmbeddedBaseMap(archive, manifest);

                ExtractEntryAtomically(
                    archive,
                    WbxGeoFormat.BaseMapEntry,
                    Path.Combine(normalizedTarget, "map.wbox"),
                    true);
                ExtractEntryAtomically(
                    archive,
                    WbxGeoFormat.BaseMetaEntry,
                    Path.Combine(normalizedTarget, "map.meta"),
                    false);
                ExtractEntryAtomically(
                    archive,
                    WbxGeoFormat.BasePreviewEntry,
                    Path.Combine(normalizedTarget, "preview.png"),
                    false);
            }

            string sidecar = GetSidecarPath(normalizedTarget);
            if (!string.Equals(
                Path.GetFullPath(packagePath),
                Path.GetFullPath(sidecar),
                StringComparison.OrdinalIgnoreCase))
            {
                string temporary = sidecar + ".tmp";
                File.Copy(packagePath, temporary, true);
                ReplaceFile(temporary, sidecar);
            }
        }

        private static WbxGeoManifest CreateManifest(
            TerrainWorldState state,
            SavedMap savedMap,
            string baseMapPath,
            byte[] elevation,
            byte[] landform,
            byte[] material)
        {
            return new WbxGeoManifest
            {
                Format = WbxGeoFormat.FormatId,
                SchemaVersion = WbxGeoFormat.SchemaVersion,
                PackageRole = "worldbox-overlay",
                ProjectId = state.ProjectId,
                CreatedUtc = state.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ModifiedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                BaseMap = new WbxGeoBaseMap
                {
                    Entry = WbxGeoFormat.BaseMapEntry,
                    Sha256 = ComputeFileSha256(baseMapPath),
                    WorldBoxSaveVersion = savedMap.saveVersion
                },
                Canvas = new WbxGeoCanvas
                {
                    WidthCells = state.Width,
                    HeightCells = state.Height,
                    WidthBlocks = savedMap.width,
                    HeightBlocks = savedMap.height,
                    CellCount = TerrainMapLimits.CountCells(state.Width, state.Height),
                    MaximumCellCount = TerrainMapLimits.MaximumCellCount,
                    Origin = "south-west",
                    RowOrder = "south-to-north",
                    CellSize = 1d
                },
                Crs = new WbxGeoCrs
                {
                    Type = "ENGCRS",
                    Name = "WorldBox / " + state.ProjectId,
                    HorizontalUnit = "worldbox_tile"
                },
                VerticalReference = new WbxGeoVerticalReference
                {
                    Datum = "worldbox-local",
                    Unit = "worldbox-height",
                    SeaLevel = state.SeaLevel,
                    StorageType = "int16",
                    NoData = TerrainElevationEncoding.NoData
                },
                Layers = new List<WbxGeoLayerDescriptor>
                {
                    CreateLayer(
                        "core.elevation",
                        "elevation",
                        WbxGeoFormat.ElevationEntry,
                        "int16",
                        elevation,
                        TerrainElevationEncoding.NoData),
                    CreateLayer(
                        "core.landform",
                        "landform",
                        WbxGeoFormat.LandformEntry,
                        "uint8",
                        landform),
                    CreateLayer(
                        "core.material",
                        "surface_material",
                        WbxGeoFormat.MaterialEntry,
                        "uint8",
                        material)
                },
                Compatibility = new WbxGeoCompatibility
                {
                    VanillaFallback = true,
                    ExternalMapName = "map.wbox",
                    UnknownOptionalModules = "preserve"
                }
            };
        }

        private static WbxGeoLayerDescriptor CreateLayer(
            string id,
            string semantic,
            string entry,
            string dataType,
            byte[] bytes,
            int? noData = null)
        {
            return new WbxGeoLayerDescriptor
            {
                Id = id,
                Semantic = semantic,
                Entry = entry,
                DataType = dataType,
                ByteOrder = "little-endian",
                RowOrder = "south-to-north",
                Sha256 = ComputeSha256(bytes),
                Module = "core",
                NoData = noData
            };
        }

        private static void ValidateManifest(WbxGeoManifest manifest)
        {
            if (manifest == null || manifest.Format != WbxGeoFormat.FormatId)
            {
                throw new InvalidDataException("File is not a WBXGEO package.");
            }

            string major = manifest.SchemaVersion?.Split('.')[0];
            if (major != "1")
            {
                throw new InvalidDataException(
                    "Unsupported WBXGEO schema version: " + manifest.SchemaVersion);
            }

            if (manifest.BaseMap == null || manifest.Canvas == null ||
                manifest.VerticalReference == null)
            {
                throw new InvalidDataException("WBXGEO manifest is missing required sections.");
            }

            if (manifest.BaseMap.Entry != WbxGeoFormat.BaseMapEntry ||
                !IsSha256(manifest.BaseMap.Sha256))
            {
                throw new InvalidDataException("WBXGEO base map descriptor is invalid.");
            }

            if (manifest.VerticalReference.StorageType != "int16" ||
                manifest.VerticalReference.NoData != TerrainElevationEncoding.NoData ||
                manifest.VerticalReference.SeaLevel == TerrainElevationEncoding.NoData)
            {
                throw new InvalidDataException(
                    "WBXGEO vertical reference must use Int16 with NODATA 9999.");
            }

            if (!TerrainMapLimits.TryValidate(
                manifest.Canvas.WidthCells,
                manifest.Canvas.HeightCells,
                out string error))
            {
                throw new InvalidDataException(error);
            }

            long declaredCells = manifest.Canvas.CellCount;
            long actualCells = TerrainMapLimits.CountCells(
                manifest.Canvas.WidthCells,
                manifest.Canvas.HeightCells);
            if (declaredCells != actualCells)
            {
                throw new InvalidDataException("WBXGEO canvas cell count is inconsistent.");
            }

            List<TerrainModuleDescriptor> modules = manifest.Modules ??
                new List<TerrainModuleDescriptor>();
            if (modules.GroupBy(module => module.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("WBXGEO contains duplicate module ids.");
            }

            foreach (TerrainModuleDescriptor module in modules)
            {
                TerrainModuleRegistry.ValidateModuleId(module.Id);
                if (module.EntryPrefix != "modules/" + module.Id + "/")
                {
                    throw new InvalidDataException("WBXGEO module entry prefix is invalid: " + module.Id);
                }
            }

            List<WbxGeoLayerDescriptor> layers = manifest.Layers ??
                new List<WbxGeoLayerDescriptor>();
            if (layers.GroupBy(layer => layer.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
                layers.GroupBy(layer => layer.Entry, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("WBXGEO contains duplicate layer ids or entries.");
            }
        }

        private static WbxGeoManifest ReadManifest(ZipArchive archive)
        {
            ZipArchiveEntry entry = archive.GetEntry(WbxGeoFormat.ManifestEntry);
            if (entry == null || entry.Length > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("WBXGEO manifest is missing or too large.");
            }

            using (Stream stream = entry.Open())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
            using (JsonTextReader jsonReader = new JsonTextReader(reader))
            {
                return JsonSerializer.CreateDefault().Deserialize<WbxGeoManifest>(jsonReader);
            }
        }

        private static WbxGeoLayerDescriptor GetCoreLayer(
            WbxGeoManifest manifest,
            string id,
            string dataType)
        {
            WbxGeoLayerDescriptor layer = manifest.Layers?.FirstOrDefault(
                item => item.Id == id && item.Module == "core");
            if (layer == null || layer.DataType != dataType ||
                string.IsNullOrWhiteSpace(layer.Entry) ||
                !layer.Entry.StartsWith("layers/", StringComparison.Ordinal) ||
                layer.Entry.Contains(".."))
            {
                throw new InvalidDataException("WBXGEO core layer is invalid: " + id);
            }

            return layer;
        }

        private static byte[] ReadCheckedEntry(
            ZipArchive archive,
            WbxGeoLayerDescriptor layer,
            int expectedLength)
        {
            ZipArchiveEntry entry = archive.GetEntry(layer.Entry);
            if (entry == null || entry.Length != expectedLength)
            {
                throw new InvalidDataException("WBXGEO layer has invalid size: " + layer.Id);
            }

            byte[] bytes = new byte[expectedLength];
            using (Stream stream = entry.Open())
            {
                ReadExactly(stream, bytes);
            }

            string hash = ComputeSha256(bytes);
            if (!string.Equals(hash, layer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("WBXGEO layer checksum failed: " + layer.Id);
            }

            return bytes;
        }

        private static void WriteRegisteredModules(
            ZipArchive archive,
            WbxGeoManifest manifest,
            TerrainModuleRegistry registry,
            TerrainWorldState state)
        {
            if (registry == null)
            {
                return;
            }

            foreach (ITerrainLabPackageModule module in registry.Modules.OrderBy(item => item.Id))
            {
                TerrainModuleRegistry.ValidateModuleId(module.Id);
                TerrainModuleDescriptor descriptor = new TerrainModuleDescriptor
                {
                    Id = module.Id,
                    SchemaVersion = module.SchemaVersion,
                    Required = module.IsRequired,
                    EntryPrefix = "modules/" + module.Id + "/"
                };
                manifest.Modules.Add(descriptor);
                module.WritePackage(
                    new TerrainModuleWriteContext(archive, module.Id, manifest.Layers),
                    state);
            }
        }

        private static void ReadRegisteredModules(
            ZipArchive archive,
            WbxGeoManifest manifest,
            TerrainModuleRegistry registry,
            TerrainWorldState state)
        {
            foreach (TerrainModuleDescriptor descriptor in manifest.Modules ??
                     Enumerable.Empty<TerrainModuleDescriptor>())
            {
                TerrainModuleRegistry.ValidateModuleId(descriptor.Id);
                if (registry == null || !registry.TryGet(descriptor.Id, out ITerrainLabPackageModule module))
                {
                    if (descriptor.Required)
                    {
                        throw new InvalidDataException(
                            "WBXGEO requires an unavailable module: " + descriptor.Id);
                    }

                    continue;
                }

                try
                {
                    module.ReadPackage(
                        new TerrainModuleReadContext(
                            archive,
                            descriptor.Id,
                            (manifest.Layers ?? new List<WbxGeoLayerDescriptor>())
                                .Where(layer => layer.Module == descriptor.Id)
                                .ToList()),
                        state,
                        descriptor);
                }
                catch when (!descriptor.Required)
                {
                    // Optional module data must not prevent loading the vanilla-compatible world.
                }
            }
        }

        private static void PreserveUnknownModules(
            ZipArchive previousArchive,
            WbxGeoManifest previousManifest,
            ZipArchive destinationArchive,
            WbxGeoManifest destinationManifest,
            TerrainModuleRegistry registry)
        {
            if (previousArchive == null || previousManifest?.Modules == null)
            {
                return;
            }

            foreach (TerrainModuleDescriptor descriptor in previousManifest.Modules)
            {
                if (registry != null && registry.Contains(descriptor.Id))
                {
                    continue;
                }

                TerrainModuleRegistry.ValidateModuleId(descriptor.Id);
                string prefix = "modules/" + descriptor.Id + "/";
                foreach (ZipArchiveEntry sourceEntry in previousArchive.Entries)
                {
                    if (!sourceEntry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                        sourceEntry.FullName.Contains(".."))
                    {
                        continue;
                    }

                    CopyArchiveEntry(sourceEntry, destinationArchive);
                }

                foreach (WbxGeoLayerDescriptor layer in previousManifest.Layers ??
                         Enumerable.Empty<WbxGeoLayerDescriptor>())
                {
                    if (layer.Module == descriptor.Id &&
                        !destinationManifest.Layers.Any(item => item.Id == layer.Id))
                    {
                        destinationManifest.Layers.Add(layer);
                    }
                }

                destinationManifest.Modules.Add(descriptor);
            }
        }

        private static void TryOpenPreviousPackage(
            string path,
            out FileStream stream,
            out ZipArchive archive,
            out WbxGeoManifest manifest)
        {
            stream = null;
            archive = null;
            manifest = null;
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
                manifest = ReadManifest(archive);
                ValidateManifest(manifest);
            }
            catch
            {
                archive?.Dispose();
                stream?.Dispose();
                archive = null;
                stream = null;
                manifest = null;
            }
        }

        private static void CopyArchiveEntry(ZipArchiveEntry source, ZipArchive destination)
        {
            ZipArchiveEntry target = destination.CreateEntry(
                source.FullName,
                CompressionLevel.Optimal);
            using (Stream input = source.Open())
            using (Stream output = target.Open())
            {
                input.CopyTo(output);
            }
        }

        private static void CopyFileToEntry(
            ZipArchive archive,
            string sourcePath,
            string entryName,
            bool compress)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                entryName,
                compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            using (FileStream input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (Stream output = entry.Open())
            {
                input.CopyTo(output);
            }
        }

        private static void CopyOptionalFileToEntry(
            ZipArchive archive,
            string sourcePath,
            string entryName)
        {
            if (File.Exists(sourcePath))
            {
                CopyFileToEntry(archive, sourcePath, entryName, false);
            }
        }

        private static void WriteBytesEntry(
            ZipArchive archive,
            string entryName,
            byte[] bytes,
            bool compress)
        {
            ZipArchiveEntry entry = archive.CreateEntry(
                entryName,
                compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            using (Stream stream = entry.Open())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static void WriteTextEntry(
            ZipArchive archive,
            string entryName,
            string value,
            CompressionLevel compression)
        {
            byte[] bytes = Utf8NoBom.GetBytes(value);
            ZipArchiveEntry entry = archive.CreateEntry(entryName, compression);
            using (Stream stream = entry.Open())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static byte[] Int16ArrayToLittleEndianBytes(short[] values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            byte[] bytes = new byte[checked(values.Length * sizeof(short))];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                ReverseElementBytes(bytes, sizeof(short));
            }

            return bytes;
        }

        private static short[] LittleEndianBytesToInt16Array(byte[] bytes)
        {
            if (bytes.Length % sizeof(short) != 0)
            {
                throw new InvalidDataException("Int16 layer byte count is invalid.");
            }

            byte[] source = bytes;
            if (!BitConverter.IsLittleEndian)
            {
                source = (byte[])bytes.Clone();
                ReverseElementBytes(source, sizeof(short));
            }

            short[] values = new short[source.Length / sizeof(short)];
            Buffer.BlockCopy(source, 0, values, 0, source.Length);
            return values;
        }

        private static void ReverseElementBytes(byte[] bytes, int elementSize)
        {
            for (int offset = 0; offset < bytes.Length; offset += elementSize)
            {
                Array.Reverse(bytes, offset, elementSize);
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of WBXGEO entry.");
                }

                offset += read;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            using (SHA256 algorithm = SHA256.Create())
            {
                return ToHex(algorithm.ComputeHash(stream));
            }
        }

        private static void ValidateEmbeddedBaseMap(
            ZipArchive archive,
            WbxGeoManifest manifest)
        {
            ZipArchiveEntry entry = archive.GetEntry(WbxGeoFormat.BaseMapEntry);
            if (entry == null)
            {
                throw new InvalidDataException("WBXGEO embedded map.wbox is missing.");
            }

            string hash;
            using (Stream stream = entry.Open())
            using (SHA256 algorithm = SHA256.Create())
            {
                hash = ToHex(algorithm.ComputeHash(stream));
            }

            if (!string.Equals(hash, manifest.BaseMap.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("WBXGEO embedded map.wbox checksum failed.");
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool hexadecimal = character >= '0' && character <= '9' ||
                                   character >= 'a' && character <= 'f' ||
                                   character >= 'A' && character <= 'F';
                if (!hexadecimal)
                {
                    return false;
                }
            }

            return true;
        }

        internal static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return ToHex(algorithm.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void ExtractEntryAtomically(
            ZipArchive archive,
            string entryName,
            string destinationPath,
            bool required)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName);
            if (entry == null)
            {
                if (required)
                {
                    throw new InvalidDataException("WBXGEO base payload is missing: " + entryName);
                }

                return;
            }

            string temporary = destinationPath + ".tmp";
            using (Stream input = entry.Open())
            using (FileStream output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                input.CopyTo(output);
            }

            ReplaceFile(temporary, destinationPath);
        }

        private static void ReplaceFile(string temporaryPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, null, true);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }

        private static string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("Save directory is required.", nameof(directory));
            }

            string fullPath = Path.GetFullPath(directory);
            if (File.Exists(fullPath))
            {
                fullPath = Path.GetDirectoryName(fullPath);
            }

            return fullPath;
        }
    }
}
