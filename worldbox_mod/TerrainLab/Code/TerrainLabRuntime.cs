using System;
using System.IO;
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
            try
            {
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
