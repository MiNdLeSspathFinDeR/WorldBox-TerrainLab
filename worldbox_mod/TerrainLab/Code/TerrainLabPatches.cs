using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using NeoModLoader.General;
using UnityEngine.UI;

namespace TerrainLab
{
    [HarmonyPatch]
    internal static class TerrainLabLoadWorldPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(SaveManager),
                "loadWorld",
                new[] { typeof(string), typeof(bool) });
        }

        private static void Prefix(string pPath)
        {
            TerrainLabRuntime.CaptureLoadDirectory(pPath);
        }
    }

    [HarmonyPatch]
    internal static class TerrainLabSaveWorldPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(SaveManager),
                nameof(SaveManager.saveWorldToDirectory),
                new[] { typeof(string), typeof(bool), typeof(bool) });
        }

        private static void Postfix(string pFolder, bool pCompress, SavedMap __result)
        {
            if (!pCompress)
            {
                return;
            }

            TerrainLabRuntime.Instance?.HandleWorldSaved(pFolder, __result);
        }
    }

    [HarmonyPatch]
    internal static class TerrainLabSaveSlotGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(SaveManager),
                nameof(SaveManager.clickSaveSlot),
                Type.EmptyTypes);
        }

        private static bool Prefix()
        {
            if (!string.IsNullOrWhiteSpace(SaveManager.currentSavePath))
            {
                return true;
            }

            UnityEngine.Debug.LogWarning(
                "[TerrainLab] Save confirmation had no selected slot; " +
                "opening the native save-slot list.");
            ScrollWindow.showWindow("saves_list");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class TerrainLabMapSizeValidationPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(MapSizeLibrary),
                nameof(MapSizeLibrary.isSizeValid),
                new[] { typeof(int) });
        }

        private static void Postfix(int pMapSize, ref bool __result)
        {
            if (__result || pMapSize <= 0)
            {
                return;
            }

            long side = (long)pMapSize * TerrainMapLimits.WorldBoxBlockSize;
            if (side <= int.MaxValue)
            {
                __result = TerrainMapLimits.TryValidate(
                    (int)side,
                    (int)side,
                    out _);
            }
        }
    }

    [HarmonyPatch]
    internal static class TerrainLabSaveWindowMapSizePatch
    {
        private static readonly FieldInfo MapSizeTextField =
            AccessTools.Field(typeof(SaveWindowIcons), "_text_map_size");

        private static readonly FieldInfo MetaDataField =
            AccessTools.Field(typeof(SaveWindowIcons), "metaData");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(SaveWindowIcons),
                nameof(SaveWindowIcons.Awake),
                Type.EmptyTypes);
        }

        private static void Postfix(SaveWindowIcons __instance)
        {
            Apply(__instance);
            __instance?.StartCoroutine(ApplyAfterVanillaLocalization(__instance));
        }

        private static IEnumerator ApplyAfterVanillaLocalization(
            SaveWindowIcons instance)
        {
            yield return null;
            Apply(instance);
        }

        private static void Apply(SaveWindowIcons instance)
        {
            Text mapSizeText =
                MapSizeTextField?.GetValue(instance) as Text;
            MapMetaData metaData =
                MetaDataField?.GetValue(instance) as MapMetaData;
            if (mapSizeText == null || metaData == null)
            {
                return;
            }

            MapSizeAsset preset =
                MapSizeLibrary.getPresetAsset(metaData.width);
            bool invalidVanillaLabel =
                string.IsNullOrWhiteSpace(mapSizeText.text) ||
                string.Equals(
                    mapSizeText.text.Trim(),
                    "-1",
                    StringComparison.Ordinal);
            if (preset != null && !invalidVanillaLabel)
            {
                return;
            }

            if (!TerrainMapLimits.TryGetCellDimensionsFromBlocks(
                    metaData.width,
                    metaData.height,
                    out int widthCells,
                    out int heightCells))
            {
                return;
            }

            LocalizedText localized =
                mapSizeText.GetComponent<LocalizedText>();
            if (localized != null)
            {
                localized.enabled = false;
            }

            string format = LM.Get(
                "terrain_lab_gallery_map_size_pixels_format");
            if (string.IsNullOrWhiteSpace(format) ||
                string.Equals(
                    format,
                    "-1",
                    StringComparison.Ordinal) ||
                string.Equals(
                    format,
                    "terrain_lab_gallery_map_size_pixels_format",
                    StringComparison.Ordinal))
            {
                format = "{0} x {1} px";
            }
            mapSizeText.text = string.Format(
                format,
                widthCells,
                heightCells);
        }
    }

    [HarmonyPatch]
    internal static class TerrainLabGeyserPulsePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(Building),
                "spawnBurstSpecial",
                new[] { typeof(int) });
        }

        private static void Postfix(Building __instance, int pAmount)
        {
            BuildingData data = __instance?.getData() as BuildingData;
            if (string.Equals(data?.asset_id, "geyser", StringComparison.Ordinal))
            {
                TerrainLabRuntime.Instance?.WaterDynamics.NotifyGeyserPulse(
                    __instance,
                    Math.Min(64, Math.Max(1, pAmount)));
            }
        }
    }

    [HarmonyPatch]
    internal static class TerrainLabWaterLayerPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(PowerLibrary),
                "drawTiles",
                new[] { typeof(WorldTile), typeof(string) });
        }

        private static void Prefix(
            WorldTile pTile,
            string pPowerID,
            out TerrainSurfaceStamp __state)
        {
            __state = default(TerrainSurfaceStamp);
            if (IsWaterLayer(pPowerID) &&
                TerrainSurfaceStamp.TryCaptureSafe(
                    pTile,
                    out TerrainSurfaceStamp previous,
                    out _))
            {
                __state = previous;
            }
        }

        private static void Postfix(
            WorldTile pTile,
            string pPowerID,
            bool __result,
            TerrainSurfaceStamp __state)
        {
            if (!__result)
            {
                return;
            }

            bool waterLayer = IsWaterLayer(pPowerID);
            TerrainLabRuntime.Instance?.WaterDynamics.NotifySurfaceLayerPainted(
                pTile,
                waterLayer,
                __state);
        }

        private static bool IsWaterLayer(string powerId)
        {
            return string.Equals(
                       powerId,
                       "tile_deep_ocean",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       powerId,
                       "tile_close_ocean",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       powerId,
                       "tile_shallow_waters",
                       StringComparison.Ordinal);
        }
    }
}
