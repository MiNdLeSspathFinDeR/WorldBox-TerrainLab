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
        public const int MaximumWorldNameLength = 80;
        public const string GeneratedElevationFileName =
            "terrainlab-elevation.tif";

        private static readonly TerrainModuleRegistry ModuleRegistry;
        private static readonly TerrainReliefService ReliefService;
        private static readonly TerrainHydrologyModule HydrologyModule;
        private static readonly TerrainWaterDynamicsModule WaterDynamicsModule;
        private static readonly TerrainWaterDynamicsService WaterDynamicsService;
        private static readonly TerrainErosionModule ErosionModule;
        private static readonly TerrainImageWorkspaceService ImageWorkspaceService;
        private static readonly TerrainVegetationSeeder VegetationSeeder;
        private static readonly FieldInfo MapStatsField =
            AccessTools.Field(typeof(MapBox), "map_stats");

        private static string _pendingLoadDirectory;

        private FieldInfo _worldLoadedField;
        private Action _worldLoadedCallback;
        private bool _initialized;

        public static TerrainLabRuntime Instance { get; private set; }

        static TerrainLabRuntime()
        {
            ModuleRegistry = new TerrainModuleRegistry();
            ReliefService = new TerrainReliefService();
            HydrologyModule = new TerrainHydrologyModule();
            WaterDynamicsModule = new TerrainWaterDynamicsModule();
            WaterDynamicsService = new TerrainWaterDynamicsService();
            ErosionModule = new TerrainErosionModule();
            ImageWorkspaceService = new TerrainImageWorkspaceService();
            VegetationSeeder = new TerrainVegetationSeeder();
            ModuleRegistry.Register(HydrologyModule);
            ModuleRegistry.Register(WaterDynamicsModule);
            ModuleRegistry.Register(ErosionModule);
        }

        public TerrainWorldState State { get; private set; }

        public TerrainHydrologyModule Hydrology => HydrologyModule;

        public TerrainReliefService Relief => ReliefService;

        public TerrainWaterDynamicsService WaterDynamics => WaterDynamicsService;

        public TerrainErosionModule Erosion => ErosionModule;

        public TerrainImageWorkspaceService ImageWorkspace =>
            ImageWorkspaceService;

        public bool HasRunningAnalysis =>
            ReliefService.IsRunning || HydrologyModule.IsRunning || ErosionModule.IsRunning;

        public bool HasUnsavedChanges =>
            State != null &&
            (State.IsDirty ||
             State.Hydrology != null &&
             State.Hydrology.IsCurrent(State) &&
             State.Hydrology.IsDirty ||
             State.WaterDynamics != null &&
             State.WaterDynamics.IsDirty ||
             State.Erosion != null &&
             State.Erosion.IsCurrent(State) &&
             State.Erosion.IsDirty);

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

        public string CurrentWorldName
        {
            get
            {
                string name = GetCurrentMapStats()?.name;
                return string.IsNullOrWhiteSpace(name) ? "WorldBox" : name;
            }
        }

        public string ExchangeDirectory => Path.Combine(
            Application.persistentDataPath,
            "TerrainLab",
            "Exchange");

        public string CurrentSyncDirectory => State == null
            ? null
            : TerrainFileSync.GetWorkspaceDirectory(ExchangeDirectory, State);

        public static void RegisterModule(ITerrainLabPackageModule module)
        {
            ModuleRegistry.Register(module);
        }

        public bool TryStartReliefAnalysis(out string error)
        {
            if (HydrologyModule.IsRunning || ErosionModule.IsRunning)
            {
                error = "Another TerrainLab analysis is already running.";
                return false;
            }

            return ReliefService.TryStartAnalysis(State, out error);
        }

        public bool TryStartHydrologyAnalysis(int streamThreshold, out string error)
        {
            if (ReliefService.IsRunning || ErosionModule.IsRunning)
            {
                error = "Another TerrainLab analysis is already running.";
                return false;
            }

            return HydrologyModule.TryStartAnalysis(State, streamThreshold, out error);
        }

        public bool TryStartErosionAnalysis(
            TerrainErosionParameters parameters,
            out string error)
        {
            if (ReliefService.IsRunning || HydrologyModule.IsRunning)
            {
                error = "Another TerrainLab analysis is already running.";
                return false;
            }

            return ErosionModule.TryStartAnalysis(State, parameters, out error);
        }

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            Instance = this;
            AttachWorldLoadedCallback();
            if (!ImageWorkspaceService.TryInitialize(
                    Application.persistentDataPath,
                    out string workspaceError))
            {
                Debug.LogError(
                    "[TerrainLab] Image workspace unavailable: " +
                    workspaceError);
            }

            _initialized = true;
        }

        private void Update()
        {
            bool changed = false;
            if (_initialized)
            {
                changed |= ReliefService.Poll(State);
                changed |= HydrologyModule.Poll(State);
                changed |= ErosionModule.Poll(State);
                changed |= WaterDynamicsService.Poll(State, HasRunningAnalysis);
                changed |= ImageWorkspaceService.Poll();
                changed |= VegetationSeeder.Poll();
            }

            if (changed)
            {
                NotifyStateChanged();
            }
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
                    WaterDynamicsService.AttachState(State);
                }
                else
                {
                    State.RefreshSemanticsFromWorld();
                }

                WbxGeoPackage.Save(directory, State, savedMap, ModuleRegistry);
                State.MarkSaved();
                HydrologyModule.MarkSaved(State);
                WaterDynamicsModule.MarkSaved(State);
                ErosionModule.MarkSaved(State);
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
            return TrySaveCurrentProject(null, out packagePath, out error);
        }

        public bool TrySaveCurrentProject(
            string worldName,
            out string packagePath,
            out string error)
        {
            packagePath = null;
            error = null;
            string previousWorldName = null;
            bool restoreWorldNameOnFailure = false;

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

                if (worldName != null)
                {
                    previousWorldName = CurrentWorldName;
                    if (!TrySetCurrentWorldName(
                            worldName,
                            out _,
                            out string nameError))
                    {
                        throw new InvalidOperationException(nameError);
                    }

                    restoreWorldNameOnFailure = true;
                }

                SavedMap savedMap = SaveManager.saveWorldToDirectory(directory);
                if (savedMap == null)
                {
                    throw new InvalidOperationException("WorldBox did not return saved map data.");
                }

                restoreWorldNameOnFailure = false;
                packagePath = WbxGeoPackage.GetSidecarPath(directory);
                if (!File.Exists(packagePath))
                {
                    throw new IOException("TerrainLab did not create the WBXGEO sidecar.");
                }

                return true;
            }
            catch (Exception exception)
            {
                MapStats mapStats = GetCurrentMapStats();
                if (restoreWorldNameOnFailure && mapStats != null)
                {
                    mapStats.name = previousWorldName;
                }

                packagePath = null;
                error = exception.Message;
                return false;
            }
        }

        public bool TrySetCurrentWorldName(
            string worldName,
            out string normalizedName,
            out string error)
        {
            if (!TryNormalizeWorldName(
                    worldName,
                    out normalizedName,
                    out error))
            {
                return false;
            }

            MapStats mapStats = GetCurrentMapStats();
            if (mapStats == null)
            {
                error = "The current WorldBox world is unavailable.";
                return false;
            }

            mapStats.name = normalizedName;
            return true;
        }

        private static MapStats GetCurrentMapStats()
        {
            return World.world == null || MapStatsField == null
                ? null
                : MapStatsField.GetValue(World.world) as MapStats;
        }

        public static bool TryNormalizeWorldName(
            string worldName,
            out string normalizedName,
            out string error)
        {
            normalizedName = worldName?.Trim();
            error = null;
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                error = "World name cannot be empty.";
                return false;
            }

            if (normalizedName.Length > MaximumWorldNameLength)
            {
                error = "World name cannot exceed " +
                        MaximumWorldNameLength + " characters.";
                return false;
            }

            for (int index = 0; index < normalizedName.Length; index++)
            {
                char character = normalizedName[index];
                if (char.IsControl(character) ||
                    character == '<' ||
                    character == '>')
                {
                    error =
                        "World name contains an unsupported character.";
                    return false;
                }
            }

            return true;
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

        public bool TryExportGisLayers(out string exportDirectory, out string error)
        {
            exportDirectory = null;
            error = null;
            try
            {
                if (State == null)
                {
                    throw new InvalidOperationException(
                        "GIS export requires an active TerrainLab project.");
                }

                exportDirectory = TerrainGisExporter.Export(ExchangeDirectory, State);
                return true;
            }
            catch (Exception exception)
            {
                exportDirectory = null;
                error = exception.Message;
                return false;
            }
        }

        public bool TryPrepareFileSync(out TerrainSyncResult result, out string error)
        {
            result = null;
            error = null;
            try
            {
                if (State == null)
                {
                    throw new InvalidOperationException(
                        "File sync requires an active TerrainLab project.");
                }

                result = TerrainFileSync.PrepareWorkspace(ExchangeDirectory, State);
                return true;
            }
            catch (Exception exception)
            {
                result = null;
                error = exception.Message;
                return false;
            }
        }

        public bool TryPullFileSync(
            TerrainSyncConflictPolicy policy,
            out TerrainSyncResult result,
            out string error)
        {
            result = null;
            error = null;
            try
            {
                if (State == null)
                {
                    throw new InvalidOperationException(
                        "File sync requires an active TerrainLab project.");
                }

                ReliefService.Cancel();
                HydrologyModule.Cancel();
                ErosionModule.Cancel();
                result = TerrainFileSync.Pull(ExchangeDirectory, State, policy);
                if (result.Applied)
                {
                    NotifyStateChanged();
                }

                return true;
            }
            catch (Exception exception)
            {
                result = null;
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
            ReliefService.Cancel();
            HydrologyModule.Cancel();
            ErosionModule.Cancel();
            WaterDynamicsService.Reset();
            VegetationSeeder.Reset();
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

                TerrainWorldState captured =
                    TerrainWorldState.CaptureCurrentWorld();
                TerrainRasterGeoreference importedGeoreference = null;
                if (!string.IsNullOrWhiteSpace(directory) &&
                    !TerrainRasterGeoreference.TryReadMapSidecar(
                        directory,
                        MapBox.width,
                        MapBox.height,
                        out importedGeoreference,
                        out string georeferenceError) &&
                    !string.IsNullOrWhiteSpace(georeferenceError))
                {
                    Debug.LogWarning(
                        "[TerrainLab] Imported georeference ignored: " +
                        georeferenceError);
                }
                string generatedElevationPath =
                    string.IsNullOrWhiteSpace(directory)
                        ? null
                        : Path.Combine(
                            directory,
                            GeneratedElevationFileName);
                if (generatedElevationPath != null &&
                    File.Exists(generatedElevationPath))
                {
                    try
                    {
                        short[] elevation = TerrainGeoTiff.ReadInt16(
                            generatedElevationPath,
                            MapBox.width,
                            MapBox.height,
                            null,
                            importedGeoreference);
                        State = TerrainWorldState.CreateFromLayers(
                            captured.ProjectId,
                            captured.CreatedUtc,
                            captured.Width,
                            captured.Height,
                            0,
                            elevation,
                            captured.Landform,
                            captured.Material,
                            captured.HorizontalMetresPerCell,
                            importedGeoreference);
                        State.ApplyElevationToWorldCache();
                        State.MarkSemanticLayersChanged();
                        Debug.Log(
                            "[TerrainLab] Loaded manually interpolated DEM from " +
                            generatedElevationPath);
                    }
                    catch (Exception generatedDemException)
                    {
                        State = captured;
                        State.SetGeoreference(importedGeoreference);
                        Debug.LogWarning(
                            "[TerrainLab] Generated DEM ignored; using vanilla " +
                            "terrain: " + generatedDemException.Message);
                    }
                }
                else
                {
                    State = captured;
                    State.SetGeoreference(importedGeoreference);
                    Debug.Log(
                        "[TerrainLab] Initialized GIS layers from the vanilla world.");
                }
            }
            catch (Exception exception)
            {
                State = null;
                Debug.LogError("[TerrainLab] Failed to initialize terrain state: " + exception);
            }
            finally
            {
                _pendingLoadDirectory = null;
                WaterDynamicsService.AttachState(State);
                VegetationSeeder.Schedule(GetCurrentMapStats());
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
            ReliefService.Cancel();
            HydrologyModule.Cancel();
            ErosionModule.Cancel();
            WaterDynamicsService.Reset();
            VegetationSeeder.Reset();
            ImageWorkspaceService.Dispose();
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
