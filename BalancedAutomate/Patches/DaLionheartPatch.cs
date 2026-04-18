using HarmonyLib;
using System;
class DaLionheartPatch
{
    static public void Apply(Harmony harmony)
    {
        var type = AccessTools.TypeByName(
            "DaLion.Core.Framework.Patchers.ObjectMinutesElapsedPatcher");
        harmony.Patch(
            original: AccessTools.Method(type, "ObjectMinutesElapsedPostfix"),
            prefix: new HarmonyMethod(typeof(DaLionheartPatch), nameof(BlockerPrefix)) { priority = HarmonyLib.Priority.First }
        );
        type = AccessTools.TypeByName(
            "DaLion.Core.Framework.Patchers.ObjectCheckForActionOnMachinePatcher");
        harmony.Patch(
            original: AccessTools.Method(type, "ObjectCheckForActionOnMachineTranspiler"),
            prefix: new HarmonyMethod(typeof(DaLionheartPatch), nameof(BlockerPrefix)) { priority = HarmonyLib.Priority.First }
        ); 
        type = AccessTools.TypeByName(
            "DaLion.Core.Framework.Patchers.ObjectOnReadyForHarvestPatcher");
        harmony.Patch(
            original: AccessTools.Method(type, "ObjectOnReadyForHarvestPostfix"),
            prefix: new HarmonyMethod(typeof(DaLionheartPatch), nameof(BlockerPrefix)) { priority = HarmonyLib.Priority.First }
        );
        type = AccessTools.TypeByName(
            "DaLion.Core.Framework.Patchers.ObjectPlacementActionPatcher");
        harmony.Patch(
            original: AccessTools.Method(type, "ObjectPlacementActionPostfix"),
            prefix: new HarmonyMethod(typeof(DaLionheartPatch), nameof(BlockerPrefix)) { priority = HarmonyLib.Priority.First }
        );
    }
    static bool BlockerPrefix(Object __instance)
    {
        return false;
    }
}