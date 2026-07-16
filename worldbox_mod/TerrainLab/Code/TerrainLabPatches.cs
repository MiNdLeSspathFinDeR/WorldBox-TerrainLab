using System;
using System.Reflection;
using HarmonyLib;

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
                __result = TerrainMapLimits.IsWithinBudget((int)side, (int)side);
            }
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
            if (pAmount > 0 &&
                string.Equals(data?.asset_id, "geyser", StringComparison.Ordinal))
            {
                TerrainLabRuntime.Instance?.WaterDynamics.NotifyGeyserPulse(
                    __instance.current_tile,
                    pAmount);
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

        private static void Postfix(WorldTile pTile, string pPowerID, bool __result)
        {
            if (!__result)
            {
                return;
            }

            bool waterLayer =
                string.Equals(pPowerID, "tile_deep_ocean", StringComparison.Ordinal) ||
                string.Equals(pPowerID, "tile_close_ocean", StringComparison.Ordinal) ||
                string.Equals(pPowerID, "tile_shallow_waters", StringComparison.Ordinal);
            TerrainLabRuntime.Instance?.WaterDynamics.NotifySurfaceLayerPainted(
                pTile,
                waterLayer);
        }
    }
}
