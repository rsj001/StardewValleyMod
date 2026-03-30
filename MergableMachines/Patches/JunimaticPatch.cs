using System.Collections.Generic;
using System;
using System.Reflection;
using HarmonyLib;
using StardewValley;
using SObject = StardewValley.Object;
class JunimaticPatch
{
    static Dictionary<Type, PropertyInfo> Cache = new Dictionary<Type, PropertyInfo>();
    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("NermNermNerm.Junimatic.ObjectMachine");
        var origin = AccessTools.Method(type, "GetRecipeFromChest");
        harmony.Patch(
            original: origin,
            postfix: new HarmonyMethod(typeof(JunimaticPatch), nameof(Postfix)) { priority = HarmonyLib.Priority.Last }
        );
    }
    public static void Postfix(object __instance, ref List<Item> __result)
    {
        var type = __instance.GetType();
        if (!Cache.TryGetValue(type, out var prop))
        {
            prop = AccessTools.Property(type, "Machine");
            Cache[type] = prop;
        }
        if (prop?.GetValue(__instance) is not SObject Machine) return;
        var MachineStack = Machine.Stack;
        if (MachineStack <= 1) return;
        if (__result == null) return;
        foreach (var item in __result)
        {
            if (item == null) continue;
            item.Stack = item.Stack * MachineStack;
        }
    }
}