using HarmonyLib;
using System.Reflection.Emit;
using System.Collections.Generic;
using BalancedAutomate;
class AlternativeTexturesPatch
{
    static public void Apply(Harmony harmony)
    {
        var type = AccessTools.TypeByName("AlternativeTextures.Framework.Patches.StandardObjects.ObjectPatch");
        harmony.Patch(
            original: AccessTools.Method(type, "DrawPrefix"),
            transpiler: new HarmonyMethod(typeof(AlternativeTexturesPatch), nameof(Transpiler)) { priority = HarmonyLib.Priority.First }
        ); // This adds drawing of ModData, also handles the logic of Auto-Stacking
    }
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        for (int i = 0; i + 1 < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Ldarg_0 &&
                codes[i + 1].opcode == OpCodes.Ldfld &&
                codes[i + 1].operand is System.Reflection.FieldInfo fi && fi.Name == "readyForHarvest")
            {
                codes[i].opcode = OpCodes.Ldc_I4_0;
                codes[i].operand = null;
                codes[i + 1].opcode = OpCodes.Ret;
                codes[i + 1].operand = null;
                return codes;
            }
        }
        ModEntry.monitor.Log("Failed to patch with Transpiler. Visual error may occur.", StardewModdingAPI.LogLevel.Error);
        return codes;
    }

}