using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TerrainLab
{
    public interface ITerrainLabPackageModule
    {
        string Id { get; }

        string SchemaVersion { get; }

        bool IsRequired { get; }

        void WritePackage(TerrainModuleWriteContext context, TerrainWorldState state);

        void ReadPackage(
            TerrainModuleReadContext context,
            TerrainWorldState state,
            TerrainModuleDescriptor descriptor);
    }

    public sealed class TerrainModuleDescriptor
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("schema_version")]
        public string SchemaVersion { get; set; }

        [JsonProperty("required")]
        public bool Required { get; set; }

        [JsonProperty("entry_prefix")]
        public string EntryPrefix { get; set; }

        [JsonProperty("metadata", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Metadata { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    public sealed class TerrainModuleRegistry
    {
        private readonly Dictionary<string, ITerrainLabPackageModule> _modules =
            new Dictionary<string, ITerrainLabPackageModule>(StringComparer.Ordinal);

        public IEnumerable<ITerrainLabPackageModule> Modules => _modules.Values;

        public void Register(ITerrainLabPackageModule module)
        {
            if (module == null)
            {
                throw new ArgumentNullException(nameof(module));
            }

            ValidateModuleId(module.Id);
            if (string.IsNullOrWhiteSpace(module.SchemaVersion))
            {
                throw new ArgumentException("Module schema version is required.", nameof(module));
            }

            if (_modules.ContainsKey(module.Id))
            {
                throw new InvalidOperationException("TerrainLab module is already registered: " + module.Id);
            }

            _modules.Add(module.Id, module);
        }

        public bool TryGet(string id, out ITerrainLabPackageModule module)
        {
            return _modules.TryGetValue(id, out module);
        }

        public bool Contains(string id)
        {
            return id != null && _modules.ContainsKey(id);
        }

        internal static void ValidateModuleId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            {
                throw new ArgumentException("Module id must contain 1 to 64 characters.");
            }

            char first = id[0];
            if (!(first >= 'a' && first <= 'z') && !(first >= '0' && first <= '9'))
            {
                throw new ArgumentException("Module id must start with a lowercase letter or digit.");
            }

            for (int index = 0; index < id.Length; index++)
            {
                char value = id[index];
                bool valid = value >= 'a' && value <= 'z' ||
                             value >= '0' && value <= '9' ||
                             value == '.' || value == '_' || value == '-';
                if (!valid)
                {
                    throw new ArgumentException(
                        "Module id may contain lowercase ASCII letters, digits, dots, underscores, and hyphens.");
                }
            }

            if (id.Contains(".."))
            {
                throw new ArgumentException("Module id may not contain '..'.");
            }
        }
    }

    public sealed class TerrainModuleWriteContext
    {
        private readonly ZipArchive _archive;
        private readonly string _prefix;
        private readonly string _moduleId;
        private readonly IList<WbxGeoLayerDescriptor> _layers;

        internal TerrainModuleWriteContext(
            ZipArchive archive,
            string moduleId,
            IList<WbxGeoLayerDescriptor> layers)
        {
            _archive = archive;
            _moduleId = moduleId;
            _prefix = "modules/" + moduleId + "/";
            _layers = layers;
        }

        public Stream CreateEntry(string relativePath, bool compress = true)
        {
            string path = _prefix + NormalizeRelativePath(relativePath);
            ZipArchiveEntry entry = _archive.CreateEntry(
                path,
                compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
            return entry.Open();
        }

        public void WriteBytes(string relativePath, byte[] data, bool compress = true)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            using (Stream stream = CreateEntry(relativePath, compress))
            {
                stream.Write(data, 0, data.Length);
            }
        }

        public void WriteJson(string relativePath, object value)
        {
            string json = JsonConvert.SerializeObject(value, Formatting.Indented);
            WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(json));
        }

        public void WriteLayerBytes(
            string layerId,
            string semantic,
            string relativePath,
            string dataType,
            byte[] data,
            int? noData = null)
        {
            if (string.IsNullOrWhiteSpace(layerId) || string.IsNullOrWhiteSpace(semantic) ||
                string.IsNullOrWhiteSpace(dataType))
            {
                throw new ArgumentException("Module layer id, semantic, and data type are required.");
            }

            string normalized = NormalizeRelativePath(relativePath);
            if (_layers.Any(layer => string.Equals(layer.Id, layerId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Package layer id is already registered: " + layerId);
            }

            WriteBytes(normalized, data);
            _layers.Add(new WbxGeoLayerDescriptor
            {
                Id = layerId,
                Semantic = semantic,
                Entry = _prefix + normalized,
                DataType = dataType,
                ByteOrder = "little-endian",
                RowOrder = "south-to-north",
                Sha256 = WbxGeoPackage.ComputeSha256(data),
                Module = _moduleId,
                NoData = noData
            });
        }

        internal static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("Package entry path is required.");
            }

            string normalized = relativePath.Replace('\\', '/').TrimStart('/');
            string[] segments = normalized.Split('/');
            if (Path.IsPathRooted(relativePath) ||
                segments.Any(segment => segment == ".." || segment.Length == 0))
            {
                throw new ArgumentException("Package entry path must stay inside the module directory.");
            }

            return normalized;
        }
    }

    public sealed class TerrainModuleReadContext
    {
        private readonly ZipArchive _archive;
        private readonly string _prefix;
        private readonly IReadOnlyList<WbxGeoLayerDescriptor> _layers;

        internal TerrainModuleReadContext(
            ZipArchive archive,
            string moduleId,
            IReadOnlyList<WbxGeoLayerDescriptor> layers)
        {
            _archive = archive;
            _prefix = "modules/" + moduleId + "/";
            _layers = layers;
        }

        public IReadOnlyList<WbxGeoLayerDescriptor> Layers => _layers;

        public Stream OpenEntry(string relativePath)
        {
            string path = _prefix + TerrainModuleWriteContext.NormalizeRelativePath(relativePath);
            return _archive.GetEntry(path)?.Open();
        }

        public T ReadJson<T>(string relativePath)
        {
            using (Stream stream = OpenEntry(relativePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("Module package entry was not found.", relativePath);
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                using (JsonTextReader jsonReader = new JsonTextReader(reader))
                {
                    return JsonSerializer.CreateDefault().Deserialize<T>(jsonReader);
                }
            }
        }
    }
}
