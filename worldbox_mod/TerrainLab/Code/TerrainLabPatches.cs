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

        private static void Postfix(string pFolder, SavedMap __result)
        {
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
}
