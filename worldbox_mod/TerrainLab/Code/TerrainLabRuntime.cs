using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TerrainLab
{
    public sealed class TerrainLabRuntime : MonoBehaviour
    {
        private static readonly TerrainModuleRegistry ModuleRegistry =
            new TerrainModuleRegistry();

        private static string _pendingLoadDirectory;

        private FieldInfo _worldLoadedField;
        private Action _worldLoadedCallback;
        private bool _initialized;

        public static TerrainLabRuntime Instance { get; private set; }

        public TerrainWorldState State { get; private set; }

        public event Action StateChanged;

        public string CurrentWorldDirectory
        {
            get
            {
                return string.IsNullOrWhiteSpace(SaveManager.currentSavePath)
                    ? null
                    : Path.GetFullPath(SaveManager.currentSavePath);
            }
        }

        public string CurrentPackagePath
        {
            get
            {
                string directory = CurrentWorldDirectory;
                return directory == null ? null : WbxGeoPackage.GetSidecarPath(directory);
            }
        }

        public string ExchangeDirectory => Path.Combine(
            Application.persistentDataPath,
            "TerrainLab",
            "Exchange");

        public static void RegisterModule(ITerrainLabPackageModule module)
        {
            ModuleRegistry.Register(module);
        }

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            Instance = this;
            AttachWorldLoadedCallback();
            _initialized = true;
        }

        public static void CaptureLoadDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _pendingLoadDirectory = null;
                return;
            }

            string fullPath = Path.GetFullPath(path);
            _pendingLoadDirectory = File.Exists(fullPath)
                ? Path.GetDirectoryName(fullPath)
                : fullPath;
        }

        public void HandleWorldSaved(string directory, SavedMap savedMap)
        {
            if (IsAutosaveDirectory(directory))
            {
                return;
            }

            try
            {
                string baseMapPath = Path.Combine(Path.GetFullPath(directory), "map.wbox");
                if (!File.Exists(baseMapPath))
                {
                    Debug.LogWarning(
                        "[TerrainLab] WBXGEO skipped because the save has no map.wbox: " +
                        directory);
                    return;
                }

                if (!TerrainMapLimits.TryValidate(MapBox.width, MapBox.height, out string error))
                {
                    Debug.LogWarning("[TerrainLab] WBXGEO was not written: " + error);
                    return;
                }

                if (State == null || !State.MatchesCurrentWorld())
                {
                    State = TerrainWorldState.CaptureCurrentWorld();
                }
                else
                {
                    State.RefreshSemanticsFromWorld();
                }

                WbxGeoPackage.Save(directory, State, savedMap, ModuleRegistry);
                Debug.Log("[TerrainLab] Saved " + WbxGeoPackage.GetSidecarPath(directory));
            }
            catch (Exception exception)
            {
                // The vanilla map has already been saved and must remain usable.
                Debug.LogError("[TerrainLab] Failed to save WBXGEO overlay: " + exception);
            }
            finally
            {
                NotifyStateChanged();
            }
        }

        public IReadOnlyList<string> GetExchangePackages(int maximumCount = 8)
        {
            if (maximumCount <= 0)
            {
                return Array.Empty<string>();
            }

            try
            {
                Directory.CreateDirectory(ExchangeDirectory);
                return Directory
                    .EnumerateFiles(ExchangeDirectory, "*.wbxgeo", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Take(maximumCount)
                    .ToArray();
            }
            catch (Exception exception)
            {
                Debug.LogError("[TerrainLab] Failed to enumerate exchange packages: " + exception);
                return Array.Empty<string>();
            }
        }

        public bool TrySaveCurrentProject(out string packagePath, out string error)
        {
            packagePath = null;
            error = null;

            try
            {
                string directory = CurrentWorldDirectory;
                if (directory == null)
                {
                    throw new InvalidOperationException(
                        "Save the WorldBox world to a slot before creating a WBXGEO project.");
                }

                if (!TerrainMapLimits.TryValidate(MapBox.width, MapBox.height, out string limitError))
                {
                    throw new InvalidOperationException(limitError);
                }

                SavedMap savedMap = SaveManager.saveWorldToDirectory(directory);
                if (savedMap == null)
                {
                    throw new InvalidOperationException("WorldBox did not return saved map data.");
                }

                packagePath = WbxGeoPackage.GetSidecarPath(directory);
                if (!File.Exists(packagePath))
                {
                    throw new IOException("TerrainLab did not create the WBXGEO sidecar.");
                }

                return true;
            }
            catch (Exception exception)
            {
                packagePath = null;
                error = exception.Message;
                return false;
            }
        }

        public bool TryExportCurrentProject(out string exportPath, out string error)
        {
            exportPath = null;
            error = null;

            if (!TrySaveCurrentProject(out string packagePath, out error))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(ExchangeDirectory);
                exportPath = CreateUniqueExportPath();
                string temporaryPath = exportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(packagePath, temporaryPath, false);
                    File.Move(temporaryPath, exportPath);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                exportPath = null;
                error = exception.Message;
                return false;
            }
        }

        public bool TryValidateCurrentProject(out TerrainWorldState packageState, out string error)
        {
            packageState = null;
            error = null;

            string packagePath = CurrentPackagePath;
            if (packagePath == null || !File.Exists(packagePath))
            {
                error = "The current world does not have a WBXGEO sidecar.";
                return false;
            }

            string baseMapPath = Path.Combine(CurrentWorldDirectory, "map.wbox");
            return WbxGeoPackage.TryLoad(
                packagePath,
                baseMapPath,
                ModuleRegistry,
                out packageState,
                out error);
        }

        public bool TryValidatePackage(
            string packagePath,
            out TerrainWorldState packageState,
            out string error)
        {
            packageState = null;
            error = null;

            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                error = "WBXGEO package was not found.";
                return false;
            }

            try
            {
                return WbxGeoPackage.TryLoad(
                    Path.GetFullPath(packagePath),
                    null,
                    ModuleRegistry,
                    out packageState,
                    out error);
            }
            catch (Exception exception)
            {
                packageState = null;
                error = exception.Message;
                return false;
            }
        }

        public bool TryImportPackage(
            string packagePath,
            out int slot,
            out string targetDirectory,
            out string error)
        {
            slot = 0;
            targetDirectory = null;
            error = null;

            if (!TryValidatePackage(packagePath, out _, out error))
            {
                return false;
            }

            string stagingDirectory = null;
            try
            {
                string savesDirectory = Path.Combine(Application.persistentDataPath, "saves");
                Directory.CreateDirectory(savesDirectory);

                for (int candidateSlot = 1; candidateSlot <= 999; candidateSlot++)
                {
                    string candidateDirectory = Path.Combine(
                        savesDirectory,
                        "save" + candidateSlot);
                    if (!Directory.Exists(candidateDirectory))
                    {
                        slot = candidateSlot;
                        targetDirectory = candidateDirectory;
                        break;
                    }
                }

                if (targetDirectory == null)
                {
                    throw new IOException("No free WorldBox save slot directory was found.");
                }

                stagingDirectory = Path.Combine(
                    savesDirectory,
                    ".terrainlab-import-" + Guid.NewGuid().ToString("N"));
                WbxGeoPackage.ImportVanillaPayload(packagePath, stagingDirectory);
                Directory.Move(stagingDirectory, targetDirectory);
                stagingDirectory = null;
                return true;
            }
            catch (Exception exception)
            {
                slot = 0;
                targetDirectory = null;
                error = exception.Message;
                return false;
            }
            finally
            {
                if (stagingDirectory != null && Directory.Exists(stagingDirectory))
                {
                    try
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                    catch (Exception cleanupException)
                    {
                        Debug.LogWarning(
                            "[TerrainLab] Failed to remove import staging directory: " +
                            cleanupException.Message);
                    }
                }
            }
        }

        private void HandleWorldLoaded()
        {
            try
            {
                if (!TerrainMapLimits.TryValidate(MapBox.width, MapBox.height, out string limitError))
                {
                    State = null;
                    Debug.LogWarning("[TerrainLab] GIS layers disabled: " + limitError);
                    return;
                }

                string directory = ResolveLoadedWorldDirectory();
                string packagePath = string.IsNullOrWhiteSpace(directory)
                    ? null
                    : WbxGeoPackage.GetSidecarPath(directory);
                string baseMapPath = string.IsNullOrWhiteSpace(directory)
                    ? null
                    : Path.Combine(directory, "map.wbox");

                if (packagePath != null && File.Exists(packagePath))
                {
                    if (WbxGeoPackage.TryLoad(
                        packagePath,
                        baseMapPath,
                        ModuleRegistry,
                        out TerrainWorldState packageState,
                        out string packageError) &&
                        packageState.Width == MapBox.width &&
                        packageState.Height == MapBox.height)
                    {
                        State = packageState;
                        State.ApplyElevationToWorldCache();
                        Debug.Log("[TerrainLab] Loaded WBXGEO project " + State.ProjectId);
                        return;
                    }

                    Debug.LogWarning(
                        "[TerrainLab] WBXGEO ignored; using vanilla terrain: " + packageError);
                }

                State = TerrainWorldState.CaptureCurrentWorld();
                Debug.Log("[TerrainLab] Initialized GIS layers from the vanilla world.");
            }
            catch (Exception exception)
            {
                State = null;
                Debug.LogError("[TerrainLab] Failed to initialize terrain state: " + exception);
            }
            finally
            {
                _pendingLoadDirectory = null;
                NotifyStateChanged();
            }
        }

        private string CreateUniqueExportPath()
        {
            string projectToken = State?.ProjectId;
            if (string.IsNullOrWhiteSpace(projectToken))
            {
                projectToken = "project";
            }
            else if (projectToken.Length > 8)
            {
                projectToken = projectToken.Substring(0, 8);
            }

            string stem = string.Format(
                "terrainlab-{0}-{1:yyyyMMdd-HHmmss}",
                projectToken,
                DateTime.UtcNow);
            string candidate = Path.Combine(ExchangeDirectory, stem + ".wbxgeo");
            int suffix = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(
                    ExchangeDirectory,
                    stem + "-" + suffix + ".wbxgeo");
                suffix++;
            }

            return candidate;
        }

        private static bool IsAutosaveDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            try
            {
                string autosavesRoot = Path.GetFullPath(
                    Path.Combine(Application.persistentDataPath, "autosaves"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return candidate.StartsWith(
                    autosavesRoot,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveLoadedWorldDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_pendingLoadDirectory))
            {
                return _pendingLoadDirectory;
            }

            if (!string.IsNullOrWhiteSpace(SaveManager.currentSavePath))
            {
                return SaveManager.currentSavePath;
            }

            return null;
        }

        private void AttachWorldLoadedCallback()
        {
            _worldLoadedField = AccessTools.Field(typeof(MapBox), "on_world_loaded");
            if (_worldLoadedField == null)
            {
                throw new MissingFieldException(typeof(MapBox).FullName, "on_world_loaded");
            }

            _worldLoadedCallback = HandleWorldLoaded;
            Action current = (Action)_worldLoadedField.GetValue(null);
            _worldLoadedField.SetValue(null, current + _worldLoadedCallback);
        }

        private void NotifyStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception exception)
            {
                Debug.LogError("[TerrainLab] UI state listener failed: " + exception);
            }
        }

        private void OnDestroy()
        {
            if (_worldLoadedField != null && _worldLoadedCallback != null)
            {
                Action current = (Action)_worldLoadedField.GetValue(null);
                _worldLoadedField.SetValue(null, current - _worldLoadedCallback);
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
