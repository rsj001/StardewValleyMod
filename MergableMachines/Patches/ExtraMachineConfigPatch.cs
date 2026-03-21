using MergableMachines;
using HarmonyLib;
using StardewValley;
using System;
class ExtraMachineConfigPatch
{
    public static void Apply(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Selph.StardewMods.ExtraMachineConfig.Utils");
        var method = AccessTools.Method(type, "GetFuelsForThisRecipe");
        harmony.Patch(
            method,
            postfix: new HarmonyMethod(
                typeof(ExtraMachineConfigPatch),
                nameof(GetFuelsForThisRecipe_Postfix)
            )
        );
    }

    public static void GetFuelsForThisRecipe_Postfix(ref object __result)
    {
        if (__result != null)
        {
            System.Collections.IList res_list = (System.Collections.IList)__result;

            for (int idx = 0; idx < res_list.Count; idx++)
            {
                object tuple = res_list[idx];

                var itemProp = tuple.GetType().GetField("Item1");
                var fuelProp = tuple.GetType().GetField("Item2");
                var item = (Item)itemProp.GetValue(tuple);

                var fuel = fuelProp.GetValue(tuple);
                var countField = fuel.GetType().GetField("count");
                int old = (int)countField.GetValue(fuel);
                countField.SetValue(fuel, old * ModEntry.stack_patch);

                var newTuple = Activator.CreateInstance(tuple.GetType(), item, fuel);
                res_list[idx] = newTuple;
            }
        }
    }
}