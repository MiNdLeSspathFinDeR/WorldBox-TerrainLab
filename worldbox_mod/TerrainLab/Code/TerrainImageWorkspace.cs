using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TerrainLab
{
    public enum TerrainImageWorkspacePhase
    {
        Stopped,
        Watching,
        Converting,
        Error
    }

    public sealed class TerrainImageFingerprint : IEquatable<TerrainImageFingerprint>
    {
        public long LastWriteUtcTicks { get; set; }

        public long Length { get; set; }

        public bool Equals(TerrainImageFingerprint other)
        {
            return other != null &&
                   LastWriteUtcTicks == other.LastWriteUtcTicks &&
                   Length == other.Length;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as TerrainImageFingerprint);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (LastWriteUtcTicks.GetHashCode() * 397) ^
                       Length.GetHashCode();
            }
        }
    }

    public sealed class TerrainImageWorkspaceService : IDisposable
    {
        private const int StateSchemaVersion = 1;
        private const int MaximumTrackedFiles = 2048;
        private const int MaximumScanFiles = 512;
        private const double ScanIntervalSeconds = 1.0;
        private const string StateFileName = ".terrainlab-workspace.json";

        private static readonly HashSet<string> ImageExtensions =
            new HashSet<string>(
                new[]
                {
                    ".bmp",
                    ".dds",
                    ".gif",
                    ".jfif",
                    ".jpeg",
                    ".jpg",
                    ".jp2",
                    ".png",
                    ".tga",
                    ".tif",
                    ".tiff",
                    ".webp"
                },
                StringComparer.OrdinalIgnoreCase);

        public const string SupportedFormatsDisplay =
            "PNG, JPG/JPEG/JFIF, TIFF/TIF, WebP, BMP, GIF, TGA, DDS, JP2";

        private readonly Dictionary<string, TerrainImageFingerprint> _processed =
            new Dictionary<string, TerrainImageFingerprint>(
                StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerrainImageFingerprint> _failed =
            new Dictionary<string, TerrainImageFingerprint>(
                StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerrainImageFingerprint> _observed =
            new Dictionary<string, TerrainImageFingerprint>(
                StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _pending = new Queue<string>();
        private readonly HashSet<string> _pendingNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerrainImageFingerprint>
            _pendingFingerprints =
                new Dictionary<string, TerrainImageFingerprint>(
                    StringComparer.OrdinalIgnoreCase);
        private readonly List<BackendInvocation> _backendCandidates =
            new List<BackendInvocation>();
        private readonly object _outputLock = new object();
        private readonly StringBuilder _standardOutput = new StringBuilder();
        private readonly StringBuilder _standardError = new StringBuilder();

        private Process _process;
        private string _activeImagePath;
        private TerrainImageFingerprint _activeFingerprint;
        private int _activeBackendIndex;
        private DateTime _nextScanUtc;
        private bool _initialized;
        private bool _disposed;

        public string WorkspaceDirectory { get; private set; }

        public string SavesDirectory { get; private set; }

        public bool IsAvailable => _initialized && !_disposed;

        public bool IsWatching { get; private set; }

        public bool IsConverting => _process != null;

        public int ConvertedCount { get; private set; }

        public int FailedCount => _failed.Count;

        public int PendingCount => _pending.Count + (IsConverting ? 1 : 0);

        public string ActiveImageName => string.IsNullOrWhiteSpace(_activeImagePath)
            ? null
            : Path.GetFileName(_activeImagePath);

        public string BackendName { get; private set; }

        public string LastMessage { get; private set; }

        public string LastError { get; private set; }

        public TerrainImageWorkspacePhase Phase
        {
            get
            {
                if (IsConverting)
                {
                    return TerrainImageWorkspacePhase.Converting;
                }

                if (!string.IsNullOrWhiteSpace(LastError))
                {
                    return TerrainImageWorkspacePhase.Error;
                }

                return IsWatching
                    ? TerrainImageWorkspacePhase.Watching
                    : TerrainImageWorkspacePhase.Stopped;
            }
        }

        public static bool IsSupportedImageExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            string normalized = extension[0] == '.'
                ? extension
                : "." + extension;
            return ImageExtensions.Contains(normalized);
        }

        public void Initialize(string persistentDataPath)
        {
            if (_initialized)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(persistentDataPath))
            {
                throw new ArgumentException(
                    "Persistent data path is required.",
                    nameof(persistentDataPath));
            }

            string terrainLabDirectory = Path.Combine(
                Path.GetFullPath(persistentDataPath),
                "TerrainLab");
            WorkspaceDirectory = Path.Combine(
                terrainLabDirectory,
                "ImageWorkspace");
            SavesDirectory = Path.Combine(
                Path.GetFullPath(persistentDataPath),
                "saves");
            Directory.CreateDirectory(WorkspaceDirectory);
            Directory.CreateDirectory(SavesDirectory);
            BuildBackendCandidates();
            LoadState();
            _nextScanUtc = DateTime.UtcNow;
            _initialized = true;
        }

        public bool TryInitialize(string persistentDataPath, out string error)
        {
            error = null;
            try
            {
                Initialize(persistentDataPath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                LastError =
                    "Image workspace could not be initialized: " + error;
                return false;
            }
        }

        public bool TrySetWatching(bool watching, out string error)
        {
            error = null;
            try
            {
                EnsureReady();
                if (IsWatching == watching)
                {
                    return true;
                }

                IsWatching = watching;
                LastError = null;
                LastMessage = watching
                    ? "Image workspace watcher enabled."
                    : "Image workspace watcher stopped.";
                _observed.Clear();
                if (!watching)
                {
                    _pending.Clear();
                    _pendingNames.Clear();
                    _pendingFingerprints.Clear();
                }

                _nextScanUtc = DateTime.UtcNow;
                SaveState();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                LastError = error;
                return false;
            }
        }

        public bool TryRetryFailed(out string error)
        {
            error = null;
            try
            {
                EnsureReady();
                if (!IsWatching)
                {
                    throw new InvalidOperationException(
                        "Enable the image workspace watcher before retrying files.");
                }

                _failed.Clear();
                _observed.Clear();
                LastError = null;
                LastMessage = "Image workspace retry requested.";
                _nextScanUtc = DateTime.UtcNow;
                SaveState();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                LastError = error;
                return false;
            }
        }

        public bool Poll()
        {
            if (!_initialized || _disposed)
            {
                return false;
            }

            try
            {
                bool changed = CompleteProcessIfReady();
                if (_process != null)
                {
                    return changed;
                }

                if (IsWatching && DateTime.UtcNow >= _nextScanUtc)
                {
                    _nextScanUtc = DateTime.UtcNow.AddSeconds(ScanIntervalSeconds);
                    changed |= ScanWorkspace();
                }

                if ((IsWatching || _pending.Count > 0) && _pending.Count > 0)
                {
                    changed |= StartNextConversion();
                }

                return changed;
            }
            catch (Exception exception)
            {
                string error = "Image workspace failed: " + exception.Message;
                bool changed = !string.Equals(
                    LastError,
                    error,
                    StringComparison.Ordinal);
                LastError = error;
                return changed;
            }
        }

        public static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            if (value.Length > 0 &&
                value.All(character =>
                    !char.IsWhiteSpace(character) && character != '"'))
            {
                return value;
            }

            StringBuilder result = new StringBuilder(value.Length + 2);
            result.Append('"');
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(character);
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch
                {
                }
                finally
                {
                    _process.Dispose();
                    _process = null;
                }
            }
        }

        private bool ScanWorkspace()
        {
            bool changed = false;
            HashSet<string> present =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> files = Directory
                .EnumerateFiles(WorkspaceDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedImage)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumScanFiles);

            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                present.Add(name);
                TerrainImageFingerprint fingerprint = CaptureFingerprint(path);
                if (fingerprint == null ||
                    Matches(_processed, name, fingerprint) ||
                    Matches(_failed, name, fingerprint) ||
                    _pendingNames.Contains(name) ||
                    string.Equals(
                        path,
                        _activeImagePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_observed.TryGetValue(
                        name,
                        out TerrainImageFingerprint previous) &&
                    previous.Equals(fingerprint))
                {
                    _pending.Enqueue(path);
                    _pendingNames.Add(name);
                    _pendingFingerprints[name] = fingerprint;
                    _observed.Remove(name);
                    changed = true;
                }
                else
                {
                    _observed[name] = fingerprint;
                }
            }

            string[] staleObservations = _observed.Keys
                .Where(name => !present.Contains(name))
                .ToArray();
            foreach (string name in staleObservations)
            {
                _observed.Remove(name);
            }

            return changed;
        }

        private bool StartNextConversion()
        {
            while (_pending.Count > 0)
            {
                string path = _pending.Dequeue();
                string name = Path.GetFileName(path);
                _pendingNames.Remove(name);
                _pendingFingerprints.TryGetValue(
                    name,
                    out TerrainImageFingerprint queuedFingerprint);
                _pendingFingerprints.Remove(name);
                TerrainImageFingerprint fingerprint = CaptureFingerprint(path);
                if (fingerprint == null)
                {
                    continue;
                }

                if (queuedFingerprint == null ||
                    !queuedFingerprint.Equals(fingerprint))
                {
                    _observed[name] = fingerprint;
                    continue;
                }

                _activeImagePath = path;
                _activeFingerprint = fingerprint;
                _activeBackendIndex = 0;
                LastError = null;
                if (TryStartAvailableBackend(out string error))
                {
                    LastMessage = "Converting " + name;
                    return true;
                }

                MarkActiveFailed(error);
                return true;
            }

            return false;
        }

        private bool TryStartAvailableBackend(out string error)
        {
            List<string> failures = new List<string>();
            while (_activeBackendIndex < _backendCandidates.Count)
            {
                BackendInvocation backend =
                    _backendCandidates[_activeBackendIndex];
                try
                {
                    StartProcess(backend);
                    BackendName = backend.DisplayName;
                    error = null;
                    return true;
                }
                catch (Exception exception)
                {
                    failures.Add(backend.DisplayName + ": " + exception.Message);
                    _activeBackendIndex++;
                }
            }

            error =
                "ImageToMap backend is unavailable. Install the current " +
                "`imagetomap` Python package. " +
                string.Join(" | ", failures);
            return false;
        }

        private void StartProcess(BackendInvocation backend)
        {
            lock (_outputLock)
            {
                _standardOutput.Clear();
                _standardError.Clear();
            }

            Process process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = backend.FileName,
                Arguments = BuildArguments(backend.PrefixArguments),
                WorkingDirectory = WorkspaceDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                AppendProcessLine(_standardOutput, args.Data);
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                AppendProcessLine(_standardError, args.Data);
            };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "The converter process did not start.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        private bool CompleteProcessIfReady()
        {
            if (_process == null)
            {
                return false;
            }

            bool exited;
            try
            {
                exited = _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                exited = true;
            }

            if (!exited)
            {
                return false;
            }

            int exitCode;
            try
            {
                _process.WaitForExit();
                exitCode = _process.ExitCode;
            }
            catch (Exception exception)
            {
                exitCode = -1;
                AppendProcessLine(_standardError, exception.Message);
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }

            string output;
            string errorOutput;
            lock (_outputLock)
            {
                output = _standardOutput.ToString().Trim();
                errorOutput = _standardError.ToString().Trim();
            }

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                MarkActiveSucceeded(LastNonEmptyLine(output));
                return true;
            }

            string failure = string.IsNullOrWhiteSpace(errorOutput)
                ? output
                : errorOutput;
            if (IsBackendUnavailable(failure) &&
                ++_activeBackendIndex < _backendCandidates.Count)
            {
                if (TryStartAvailableBackend(out string fallbackError))
                {
                    LastMessage =
                        "Retrying " + Path.GetFileName(_activeImagePath) +
                        " with " + BackendName;
                    return true;
                }

                failure = fallbackError;
            }

            MarkActiveFailed(
                string.IsNullOrWhiteSpace(failure)
                    ? "ImageToMap exited with code " + exitCode + "."
                    : LastNonEmptyLine(failure));
            return true;
        }

        private void MarkActiveSucceeded(string message)
        {
            string name = Path.GetFileName(_activeImagePath);
            _processed[name] = _activeFingerprint;
            _failed.Remove(name);
            ConvertedCount++;
            LastError = null;
            LastMessage = string.IsNullOrWhiteSpace(message)
                ? "Converted " + name
                : message;
            ClearActive();
            TrimTrackingState();
            SaveState();
        }

        private void MarkActiveFailed(string error)
        {
            string name = Path.GetFileName(_activeImagePath);
            if (!string.IsNullOrWhiteSpace(name) && _activeFingerprint != null)
            {
                _failed[name] = _activeFingerprint;
            }

            LastError = string.IsNullOrWhiteSpace(error)
                ? "Image conversion failed."
                : error;
            LastMessage = null;
            ClearActive();
            TrimTrackingState();
            SaveState();
        }

        private void ClearActive()
        {
            _activeImagePath = null;
            _activeFingerprint = null;
            _activeBackendIndex = 0;
        }

        private string BuildArguments(string prefix)
        {
            StringBuilder arguments = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                arguments.Append(prefix);
                arguments.Append(' ');
            }

            arguments.Append(QuoteArgument(_activeImagePath));
            arguments.Append(" --no-config --algorithm terrain --palette safe");
            arguments.Append(" --fit-budget --save-to-game --strict");
            arguments.Append(" --game-saves-dir ");
            arguments.Append(QuoteArgument(SavesDirectory));
            return arguments.ToString();
        }

        private void BuildBackendCandidates()
        {
            _backendCandidates.Clear();
            string directBackend =
                Environment.GetEnvironmentVariable("TERRAINLAB_IMAGETOMAP");
            if (!string.IsNullOrWhiteSpace(directBackend))
            {
                AddBackend(directBackend, string.Empty, "configured imagetomap");
            }

            AddBackend("imagetomap.exe", string.Empty, "imagetomap");
            AddBackend("imagetomap", string.Empty, "imagetomap");

            string configuredPython =
                Environment.GetEnvironmentVariable("TERRAINLAB_PYTHON");
            if (!string.IsNullOrWhiteSpace(configuredPython))
            {
                AddBackend(
                    configuredPython,
                    "-m imagetomap",
                    "configured Python");
            }

            AddBackend("py.exe", "-3 -m imagetomap", "Python launcher");
            AddBackend("python.exe", "-m imagetomap", "Python");
            AddBackend("python3", "-m imagetomap", "Python 3");
            AddBackend("python", "-m imagetomap", "Python");
        }

        private void AddBackend(string fileName, string prefix, string displayName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                _backendCandidates.Any(item =>
                    string.Equals(
                        item.FileName,
                        fileName,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.PrefixArguments, prefix, StringComparison.Ordinal)))
            {
                return;
            }

            _backendCandidates.Add(
                new BackendInvocation(fileName, prefix, displayName));
        }

        private void LoadState()
        {
            string path = GetStatePath();
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length > 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "Image workspace state exceeds 1 MiB.");
                }

                WorkspaceStateDocument state =
                    JsonConvert.DeserializeObject<WorkspaceStateDocument>(
                        File.ReadAllText(path));
                if (state == null || state.SchemaVersion != StateSchemaVersion)
                {
                    return;
                }

                IsWatching = state.Watching;
                ConvertedCount = Math.Max(0, state.ConvertedCount);
                CopyValidFingerprints(state.Processed, _processed);
                CopyValidFingerprints(state.Failed, _failed);
            }
            catch (Exception exception)
            {
                IsWatching = false;
                LastError =
                    "Image workspace state could not be loaded: " +
                    exception.Message;
            }
        }

        private void SaveState()
        {
            if (!_initialized && string.IsNullOrWhiteSpace(WorkspaceDirectory))
            {
                return;
            }

            WorkspaceStateDocument state = new WorkspaceStateDocument
            {
                SchemaVersion = StateSchemaVersion,
                Watching = IsWatching,
                ConvertedCount = ConvertedCount,
                Processed = new Dictionary<string, TerrainImageFingerprint>(
                    _processed,
                    StringComparer.OrdinalIgnoreCase),
                Failed = new Dictionary<string, TerrainImageFingerprint>(
                    _failed,
                    StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(
                GetStatePath(),
                JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        private void CopyValidFingerprints(
            IDictionary<string, TerrainImageFingerprint> source,
            IDictionary<string, TerrainImageFingerprint> target)
        {
            if (source == null)
            {
                return;
            }

            foreach (KeyValuePair<string, TerrainImageFingerprint> pair in
                     source.Take(MaximumTrackedFiles))
            {
                if (pair.Value == null ||
                    string.IsNullOrWhiteSpace(pair.Key) ||
                    !string.Equals(
                        pair.Key,
                        Path.GetFileName(pair.Key),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                target[pair.Key] = pair.Value;
            }
        }

        private void TrimTrackingState()
        {
            TrimDictionary(_processed);
            TrimDictionary(_failed);
        }

        private static void TrimDictionary(
            IDictionary<string, TerrainImageFingerprint> values)
        {
            while (values.Count > MaximumTrackedFiles)
            {
                string key = values.Keys.First();
                values.Remove(key);
            }
        }

        private static bool Matches(
            IDictionary<string, TerrainImageFingerprint> values,
            string name,
            TerrainImageFingerprint fingerprint)
        {
            return values.TryGetValue(name, out TerrainImageFingerprint known) &&
                   known.Equals(fingerprint);
        }

        private static TerrainImageFingerprint CaptureFingerprint(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0)
                {
                    return null;
                }

                return new TerrainImageFingerprint
                {
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Length = info.Length
                };
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool IsSupportedImage(string path)
        {
            return IsSupportedImageExtension(Path.GetExtension(path)) &&
                   !string.Equals(
                       Path.GetFileName(path),
                       "preview.png",
                       StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(
                       Path.GetFileName(path),
                       "preview_small.png",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBackendUnavailable(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            string normalized = error.ToLowerInvariant();
            return normalized.Contains("no module named imagetomap") ||
                   normalized.Contains("unrecognized arguments: --fit-budget") ||
                   normalized.Contains("unknown option --fit-budget");
        }

        private static string LastNonEmptyLine(string value)
        {
            string[] lines = value.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? value.Trim() : lines[lines.Length - 1].Trim();
        }

        private void AppendProcessLine(StringBuilder target, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            lock (_outputLock)
            {
                if (target.Length < 64 * 1024)
                {
                    target.AppendLine(value);
                }
            }
        }

        private string GetStatePath()
        {
            return Path.Combine(WorkspaceDirectory, StateFileName);
        }

        private void EnsureReady()
        {
            if (!_initialized || _disposed)
            {
                throw new InvalidOperationException(
                    "Image workspace service is not available.");
            }

            Directory.CreateDirectory(WorkspaceDirectory);
            Directory.CreateDirectory(SavesDirectory);
        }

        private sealed class BackendInvocation
        {
            public BackendInvocation(
                string fileName,
                string prefixArguments,
                string displayName)
            {
                FileName = fileName;
                PrefixArguments = prefixArguments;
                DisplayName = displayName;
            }

            public string FileName { get; }

            public string PrefixArguments { get; }

            public string DisplayName { get; }
        }

        private sealed class WorkspaceStateDocument
        {
            public int SchemaVersion { get; set; }

            public bool Watching { get; set; }

            public int ConvertedCount { get; set; }

            public Dictionary<string, TerrainImageFingerprint> Processed { get; set; }

            public Dictionary<string, TerrainImageFingerprint> Failed { get; set; }
        }
    }
}
