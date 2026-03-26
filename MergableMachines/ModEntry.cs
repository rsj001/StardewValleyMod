using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;

using StardewValley;
using StardewValley.Extensions;
using StardewValley.GameData.Machines;
using StardewValley.GameData.WildTrees;
using StardewValley.Inventories;
using HarmonyLib;

using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;

using StardewValley.Objects;
using StardewValley.ItemTypeDefinitions;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;
using Object = System.Object;
using StardewValley.TokenizableStrings;

namespace MergableMachines
{
    public enum ForceStackMode
    {
        Disabled,
        RequireKey,
        Always
    }
    public sealed class ModConfig
    {

        public int gmcmInterval { get; set; } = 60;
        public int maxStack { get; set; } = 99;
        public float NumberOpacity { get; set; } = 0.9f;
        public ForceStackMode forceStackMode { get; set; } = ForceStackMode.RequireKey;
        public bool stackTappers { get; set; } = false;
        public HashSet<string> blackList { get; set; } = new HashSet<string>();
        public KeybindList forceStackKeybind = new KeybindList(SButton.Q);
        public KeybindList quickStack999Keybind = new KeybindList();
        public KeybindList quickStack25Keybind = new KeybindList(new Keybind(SButton.LeftShift, SButton.LeftControl), new Keybind(SButton.RightShift, SButton.RightControl));
        public KeybindList quickStack5Keybind = new KeybindList(new Keybind(SButton.LeftShift), new Keybind(SButton.RightShift));

        //  "(BC)254", // Ostrich incubator
        //  "(BC)156", // slime incubator
        //  "(BC)101", // incubator
    }
    public interface IGenericModConfigMenuApi
    {
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
        void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string> tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string> formatValue = null, string fieldId = null);
        void AddKeybindList(IManifest mod, Func<KeybindList> getValue, Action<KeybindList> setValue, Func<string> name, Func<string> tooltip = null, string fieldId = null);
        void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string> tooltip = null, string[] allowedValues = null, Func<string, string> formatAllowedValue = null, string fieldId = null);

        void AddNumberOption(IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name, Func<string> tooltip = null, float? min = null, float? max = null, float? interval = null, Func<float, string> formatValue = null, string fieldId = null);
        void AddSectionTitle(IManifest mod, Func<string> text, Func<string> tooltip = null);
        void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string> tooltip = null, string fieldId = null);
        void Unregister(IManifest mod);
    }

    public class ModEntry : Mod
    {
        static Harmony harmony = null!;
        private static ModConfig config = new ModConfig();
        public static HashSet<string> whiteList = new HashSet<string>();
        static IModHelper helper = null!;
        public static IMonitor monitor = null!;
        public override void Entry(IModHelper init_helper)
        {
            helper = init_helper;
            monitor = this.Monitor;
            harmony = new Harmony(this.ModManifest.UniqueID);
            config = helper.ReadConfig<ModConfig>();
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.pressActionButton)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(pressActionButton_Prefix)) { priority = HarmonyLib.Priority.Last }
            );  // This patches mergable machines
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.performRemoveAction)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(performRemoveAction_Prefix)) { priority = HarmonyLib.Priority.Last }
            ); // This patches extra machine debris
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), new Type[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(draw_Postfix))
            ); // This patches tiny digit
            harmony.Patch(
                original: AccessTools.Method(typeof(WoodChipper), nameof(WoodChipper.draw), new Type[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(draw_Postfix))
            ); // This patches tiny digit
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.OutputMachine)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(OutputMachine_Postfix)) { priority = HarmonyLib.Priority.Last }
            ); // This patches machine output, giving (xStack)
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.PlaceInMachine)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(PlaceInMachine_Prefix)) { priority = HarmonyLib.Priority.Last }
            ); // This patches machine input, requires (xStack)
            harmony.Patch(
                original: AccessTools.Method(typeof(Tree), nameof(Tree.UpdateTapperProduct)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(UpdateTapperProduct_Postfix)) { priority = HarmonyLib.Priority.Last }
            );

            if (Helper.ModRegistry.IsLoaded("Selph.ExtraMachineConfig"))
            { // Compatibility Patch
                monitor.Log("Selph.ExtraMachineConfig detected, patching GetFuelsForThisRecipe", LogLevel.Info);
                ExtraMachineConfigPatch.Apply(harmony);
            }

            if (Helper.ModRegistry.IsLoaded("NermNermNerm.Junimatic"))
            {
                monitor.Log("NermNermNerm.Junimatic detected, patching GetRecipeFromChest", LogLevel.Info);
                JunimaticPatch.Patch(harmony);
            }
            helper.Events.GameLoop.UpdateTicked += onUpdateTicked;
            helper.Events.Content.AssetReady += onAssetReady;
            helper.Events.GameLoop.GameLaunched += (s, e) => reloadGMCM();
            helper.Events.GameLoop.SaveLoaded += onSaveLoaded;
        }

        void onSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!Context.IsMainPlayer) return;
            LegacyMigration.checkAll();
        }


        static public int stack_patch = 1;

        public static bool PlaceInMachine_Prefix(SObject __instance, ref bool __result, MachineData machineData, Item inputItem, bool probe, Farmer who, bool showMessages = true, bool playSounds = true)
        {
            __result = _PlaceInMachine(__instance, machineData, inputItem, probe, who, showMessages, playSounds);
            return false;
        }
        public static bool _PlaceInMachine(SObject __instance, MachineData origin_machineData, Item inputItem, bool probe, Farmer who, bool showMessages = true, bool playSounds = true)
        {
            var heldObject = __instance.heldObject;
            var lastInputItem = __instance.lastInputItem;
            ref var autoLoadFrom = ref SObject.autoLoadFrom;
            int machine_stack = __instance.Stack;
            ref var CurrentParsedItemCount = ref SObject.CurrentParsedItemCount;

            var CloneMethod = typeof(object).GetMethod(
                "MemberwiseClone",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            T Clone<T>(T obj)
            {
                return (T)CloneMethod.Invoke(obj, null);
            }
            var machineData = Clone(origin_machineData);

            if (machineData == null || inputItem == null)
            {
                return false;
            }
            if (heldObject.Value != null)
            {
                if (!machineData.AllowLoadWhenFull)
                {
                    return false;
                }
                if (inputItem.QualifiedItemId == lastInputItem.Value?.QualifiedItemId)
                {
                    return false;
                }
            }

            machineData.AdditionalConsumedItems = machineData.AdditionalConsumedItems?.Select(p => Clone(p)).ToList();
            machineData.OutputRules = machineData.OutputRules?
            .Select(rule =>
            {
                var ruleCopy = Clone(rule);
                ruleCopy.Triggers = ruleCopy.Triggers?.Select(t => Clone(t)).ToList();
                return ruleCopy;
            }).ToList();
            if (machineData?.AdditionalConsumedItems is not null)
                foreach (var item in machineData.AdditionalConsumedItems)
                {
                    if (item.RequiredCount == 0) item.RequiredCount = 1;
                    item.RequiredCount *= machine_stack;
                }
            if (machineData?.OutputRules is not null)
                foreach (MachineOutputRule curRule in machineData.OutputRules)
                    foreach (MachineOutputTriggerRule curTrigger in curRule.Triggers)
                    {
                        if (curTrigger.RequiredCount == 0) curTrigger.RequiredCount = 1;
                        curTrigger.RequiredCount *= machine_stack;
                    }


            if (!MachineDataUtility.HasAdditionalRequirements(autoLoadFrom ?? who.Items, machineData.AdditionalConsumedItems, out var failedRequirement))
            {
                if (showMessages && failedRequirement.InvalidCountMessage != null && !probe && autoLoadFrom == null)
                {
                    CurrentParsedItemCount = failedRequirement.RequiredCount;
                    Game1.showRedMessage(TokenParser.ParseText(failedRequirement.InvalidCountMessage, null, __instance.ParseItemCount));
                    who.ignoreItemConsumptionThisFrame = true;
                }
                return false;
            }
            GameLocation location = __instance.Location;
            if (!MachineDataUtility.TryGetMachineOutputRule(__instance, machineData, MachineOutputTrigger.ItemPlacedInMachine, inputItem, who, location, out var outputRule, out var triggerRule, out var outputRuleIgnoringCount, out var triggerIgnoringCount))
            {
                if (showMessages && !probe && autoLoadFrom == null)
                {
                    if (outputRuleIgnoringCount != null)
                    {
                        string invalidCountMessage = outputRuleIgnoringCount.InvalidCountMessage ?? machineData.InvalidCountMessage;
                        if (!string.IsNullOrWhiteSpace(invalidCountMessage))
                        {
                            CurrentParsedItemCount = triggerIgnoringCount.RequiredCount;
                            Game1.showRedMessage(TokenParser.ParseText(invalidCountMessage, null, __instance.ParseItemCount));
                            who.ignoreItemConsumptionThisFrame = true;
                        }
                    }
                    else if (machineData.InvalidItemMessage != null && GameStateQuery.CheckConditions(machineData.InvalidItemMessageCondition, location, who, null, who.ActiveObject))
                    {
                        Game1.showRedMessage(TokenParser.ParseText(machineData.InvalidItemMessage));
                        who.ignoreItemConsumptionThisFrame = true;
                    }
                }
                return false;
            }
            if (probe)
            {
                return true;
            }
            stack_patch = machine_stack;
            // this seems unstable but it works
            if (!__instance.OutputMachine(machineData, outputRule, inputItem, who, location, probe))
            {
                stack_patch = 1;
                return false;
            }
            stack_patch = 1;
            if (machineData.AdditionalConsumedItems != null)
            {
                IInventory inventory = autoLoadFrom ?? who.Items;
                foreach (MachineItemAdditionalConsumedItems additionalRequirement in machineData.AdditionalConsumedItems)
                {
                    inventory.ReduceId(additionalRequirement.ItemId, additionalRequirement.RequiredCount);
                }
            }
            if (triggerRule.RequiredCount > 0)
            {
                SObject.ConsumeInventoryItem(who, inputItem, triggerRule.RequiredCount);
            }
            if (machineData.LoadEffects != null)
            {
                foreach (MachineEffects effect in machineData.LoadEffects)
                {
                    if (__instance.PlayMachineEffect(effect, playSounds))
                    {
                        AccessTools.Field(typeof(SObject), "_machineAnimation").SetValue(__instance, effect);
                        AccessTools.Field(typeof(SObject), "_machineAnimationLoop").SetValue(__instance, false);
                        AccessTools.Field(typeof(SObject), "_machineAnimationIndex").SetValue(__instance, 0);
                        AccessTools.Field(typeof(SObject), "_machineAnimationFrame").SetValue(__instance, -1);
                        AccessTools.Field(typeof(SObject), "_machineAnimationInterval").SetValue(__instance, 0);
                        break;
                    }
                }
            }
            AccessTools.Method(typeof(SObject), "playCustomMachineLoadEffects").Invoke(__instance, null);
            MachineDataUtility.UpdateStats(machineData.StatsToIncrementWhenLoaded, inputItem, 1);
            return true;
        }

        public static bool pressActionButton_Prefix(KeyboardState currentKBState, MouseState currentMouseState, GamePadState currentPadState)
        {
            if (Game1.IsChatting || Game1.dialogueTyping || Game1.dialogueUp) return true;
            if (Game1.player.ActiveObject == null) return true;
            if (!Game1.player.UsingTool && (!Game1.eventUp || (Game1.currentLocation.currentEvent != null && Game1.currentLocation.currentEvent.playerControlSequence)) && !Game1.fadeToBlack)
            {
                if (!whiteList.Contains(Game1.player.ActiveObject.QualifiedItemId)) return true;
                // only check white list when stacking, check moddata when drawing
                // machine stack only!
                SObject item = Game1.player.ActiveObject;
                Vector2 cursorTile = new Vector2(Game1.getOldMouseX() + Game1.viewport.X, Game1.getOldMouseY() + Game1.viewport.Y) / 64f;
                int x = (int)cursorTile.X * 64, y = (int)cursorTile.Y * 64;
                if (Game1.currentLocation.objects.TryGetValue(new Vector2(x / 64, y / 64), out SObject obj))
                {
                    if (obj.QualifiedItemId == item.QualifiedItemId)
                    {
                        bool refuel = false;
                        if (obj.readyForHarvest.Value || obj.heldObject.Value != null)
                        {
                            if (obj.IsTapper() && !config.stackTappers)
                            {
                                Game1.showRedMessage(helper.Translation.Get("tree_tired"));
                                return true;
                            }
                            else if (config.forceStackMode == ForceStackMode.Always || (config.forceStackKeybind.IsDown() && config.forceStackMode == ForceStackMode.RequireKey))
                            {
                                obj.heldObject.Value = null;
                                obj.readyForHarvest.Value = false;
                                obj.ResetParentSheetIndex();
                                refuel = true;
                            }
                            else
                            {
                                Game1.showRedMessage(helper.Translation.Get("machine_occupied"));
                                return true;
                            }
                        }

                        int amount = 1;
                        if (config.quickStack999Keybind.IsDown())
                        {
                            amount = 999;
                        }
                        else if (config.quickStack25Keybind.IsDown())
                        {
                            amount = 25;
                        }
                        else if (config.quickStack5Keybind.IsDown())
                        {
                            amount = 5;
                        }
                        amount = Math.Min(amount, config.maxStack - obj.Stack);
                        amount = Math.Min(amount, item.Stack);
                        if (amount <= 0)
                        {
                            Game1.showRedMessage(String.Format(helper.Translation.Get("hit_max_stack"), config.maxStack));
                            return true;
                        }
                        obj.Stack += amount;
                        obj.modData["rsjww.MergableMachines.MyMachine"] = "1";
                        if (item.Stack != amount)
                        {
                            item.Stack -= amount;
                        }
                        else
                        {
                            item.Stack -= amount - 1;
                            Game1.player.reduceActiveItemByOne();
                        }
                        if (refuel)
                        {
                            Farmer who = Game1.GetPlayer(obj.owner.Value) ?? Game1.player;
                            GameLocation location = obj.Location;
                            MachineData machineData = obj.GetMachineData();
                            if (MachineDataUtility.TryGetMachineOutputRule(obj, machineData, MachineOutputTrigger.MachinePutDown, null, who, location, out var outputRule, out var _, out var _, out var _))
                            {
                                obj.OutputMachine(machineData, outputRule, null, who, location, probe: false);
                            }
                            if (obj.IsTapper() && location.terrainFeatures.TryGetValue(obj.tileLocation.Value, out var terrainFeature) && terrainFeature is Tree tree)
                            {
                                tree.UpdateTapperProduct(obj);
                            }
                        }
                        Game1.playSound("woodyStep");
                        // ???
                        return false;
                    }
                }
            }
            return true;
        }

        public static void draw_Postfix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha)
        {
            if (__instance.Stack > 1 && __instance.tileLocation is not null)
            {
                if (alpha != 1f)
                {
                    // Not placed yet
                    return;
                }
                // LegacyMigration.checkOne(__instance);
                if (!__instance.modData.TryGetValue("rsjww.MergableMachines.MyMachine", out var s) || s != "1") return;
                // Credits: Combine Machine
                float Transparency = alpha * config.NumberOpacity;
                if (Transparency > 0f)
                {
                    Color RenderColor = Color.White * Transparency;
                    float draw_layer = Math.Max(0f, (float)((y + 1) * 64 - 24) / 10000f) + (float)x * 1E-05f;
                    float DrawLayerOffset = 1E-05f; // The SpriteBatch LayerDepth needs to be slightly larger than the layer depth used for the bigCraftable texture to avoid z-fighting

                    // For Tapper and Mushroom Log:


                    // end


                    Vector2 TopLeftTilePosition = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * Game1.tileSize, y * Game1.tileSize));
                    Vector2 BottomRightTilePosition = Game1.GlobalToLocal(Game1.viewport, new Vector2((x + 1) * Game1.tileSize - 1, (y + 1) * Game1.tileSize - 1));
                    int digit_num = (int)Math.Log10(__instance.Stack) + 1;
                    float Scale = 3;
                    float QuantityWidth = 5 * Scale * digit_num;
                    Vector2 QuantityTopLeftPosition = new Vector2(BottomRightTilePosition.X - QuantityWidth, BottomRightTilePosition.Y - 7 - Game1.tileSize / 4);
                    //  Crab pots have an animation that makes them float up and down, and they're sort of shifted below the tile they're actually on, so shift our number down as well to compensate
                    // if (__instance is CrabPot)
                    //     QuantityTopLeftPosition.Y += Game1.tileSize - Game1.tileSize / 8;

                    Utility.drawTinyDigits(__instance.Stack, spriteBatch, QuantityTopLeftPosition, Scale, draw_layer + DrawLayerOffset, RenderColor);
                }
            }
        }

        public static bool performRemoveAction_Prefix(SObject __instance)
        {
            if (__instance.Stack > 1)
            {
                // LegacyMigration.checkOne(__instance);
                if (!__instance.modData.TryGetValue("rsjww.MergableMachines.MyMachine", out var s) || s != "1") return true;
                SObject CombinedRefund = (SObject)ItemRegistry.Create(__instance.QualifiedItemId, __instance.Stack - 1);
                Game1.createMultipleItemDebris(CombinedRefund, __instance.TileLocation * 64f, (Game1.player.FacingDirection + 2) % 4);
            }
            return true;
        }

        public void onAssetReady(object? sender, AssetReadyEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Machines"))
            {
                reloadGMCM();
            }
        }

        public static void OutputMachine_Postfix(SObject __instance, MachineData machine, MachineOutputRule outputRule, Item inputItem, Farmer who, GameLocation location, bool probe, bool heldObjectOnly = false)
        {
            if (!probe)
            {
                if (__instance.heldObject.Value != null)
                    __instance.heldObject.Value.Stack *= __instance.Stack;
                // hie hie!
            }
        }

        public static void UpdateTapperProduct_Postfix(Tree __instance, SObject tapper, SObject previousOutput = null, bool onlyPerformRemovals = false)
        {
            if (tapper == null)
            {
                return;
            }
            WildTreeData data = __instance.GetData();
            if (data == null)
            {
                return;
            }
            float timeMultiplier = 1f;
            foreach (string contextTag in tapper.GetContextTags())
            {
                if (contextTag.StartsWithIgnoreCase("tapper_multiplier_") && float.TryParse(contextTag.Substring("tapper_multiplier_".Length), out var multiplier))
                {
                    timeMultiplier = 1f / multiplier;
                    break;
                }
            }
            Random random = Utility.CreateRandom(Game1.uniqueIDForThisGame, Game1.stats.DaysPlayed, 73137.0, (double)__instance.Tile.X * 9.0, (double)__instance.Tile.Y * 13.0);
            var method = AccessTools.Method(typeof(Tree), "TryGetTapperOutput");
            object[] args = new object[] {
                data.TapItems,
                previousOutput?.ItemId,
                random,
                timeMultiplier,
                null, // output
                0     // minutesUntilReady
            };
            var result = (bool)method.Invoke(__instance, args);
            var output = (Item)args[4];
            var minutesUntilReady = (int)args[5];
            if (result && (!onlyPerformRemovals || output == null))
            {
                if (tapper.heldObject.Value != null)
                    tapper.heldObject.Value.Stack *= tapper.Stack;
                // This is it!
            }

        }

        private void reloadGMCM()
        {
            string G(string x) => helper.Translation.Get(x);
            var configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null) return;
            configMenu.Unregister(this.ModManifest);
            configMenu.Register(
                mod: this.ModManifest,
                reset: () => config = new ModConfig(),
                save: () => this.Helper.WriteConfig(config)
            );

            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => G("gmcm.general")
            );

            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => G("gmcm.max_stack"),
                tooltip: () => G("gmcm.max_stack.tooltip"),
                getValue: () => config.maxStack,
                setValue: (int val) => config.maxStack = val,
                min: 1,
                max: 999,
                interval: 1
            );
            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => G("gmcm.opacity"),
                tooltip: () => G("gmcm.opacity.tooltip"),
                getValue: () => (float)config.NumberOpacity,
                setValue: (float val) => config.NumberOpacity = val,
                min: 0f,
                max: 1.0f,
                interval: 0.01f
            );

            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => G("gmcm.force_stack")
            );

            configMenu.AddTextOption(
                mod: this.ModManifest,
                name: () => G("gmcm.force_stack_mode"),
                tooltip: () => G("gmcm.force_stack_mode.tooltip"),
                allowedValues: Enum.GetNames(typeof(ForceStackMode)),
                formatAllowedValue: val =>
                {
                    return val switch
                    {
                        "Disabled" => G("gmcm.disable"),
                        "RequireKey" => G("gmcm.require_key"),
                        "Always" => G("gmcm.always"),
                        _ => val
                    };
                },
                getValue: () => config.forceStackMode.ToString(),
                setValue: val => config.forceStackMode = (ForceStackMode)Enum.Parse(typeof(ForceStackMode), val)
            );

            configMenu.AddKeybindList(
               mod: this.ModManifest,
               name: () => G("gmcm.force_stack_keybind"),
               tooltip: () => G("gmcm.force_stack_keybind.tooltip"),
               getValue: () => config.forceStackKeybind,
               setValue: value => config.forceStackKeybind = value
           );

            configMenu.AddBoolOption(
                 mod: this.ModManifest,
                 name: () => G("gmcm.stack_tappers"),
                 tooltip: () => G("gmcm.stack_tappers.tooltip"),
                 getValue: () => config.stackTappers,
                 setValue: value => config.stackTappers = value
            );

            configMenu.AddSectionTitle(
                 mod: this.ModManifest,
                 text: () => G("gmcm.quickstack")
            );
            configMenu.AddKeybindList(
                 mod: this.ModManifest,
                 name: () => G("gmcm.quickstack999_keybind"),
                 tooltip: () => G("gmcm.quickstack999_keybind.tooltip"),
                 getValue: () => config.quickStack999Keybind,
                 setValue: value => config.quickStack999Keybind = value
            );
            configMenu.AddKeybindList(
                 mod: this.ModManifest,
                 name: () => G("gmcm.quickstack25_keybind"),
                 tooltip: () => G("gmcm.quickstack25_keybind.tooltip"),
                 getValue: () => config.quickStack25Keybind,
                 setValue: value => config.quickStack25Keybind = value
            );
            configMenu.AddKeybindList(
                 mod: this.ModManifest,
                 name: () => G("gmcm.quickstack5_keybind"),
                 tooltip: () => G("gmcm.quickstack5_keybind.tooltip"),
                 getValue: () => config.quickStack5Keybind,
                 setValue: value => config.quickStack5Keybind = value
            );

            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => G("gmcm.machine")
            );

            foreach ((string rawId, MachineData machineData) in DataLoader.Machines(Game1.content))
            {
                ParsedItemData data = ItemRegistry.GetData(rawId);
                if (data is null) continue;
                if (data.QualifiedItemId == "(BC)254") continue;
                if (data.QualifiedItemId == "(BC)156") continue;
                if (data.QualifiedItemId == "(BC)101") continue;
                // hardcode blacklist: incubator
                if (!config.blackList.Contains(data.QualifiedItemId))
                {
                    if (!whiteList.Contains(data.QualifiedItemId))
                    {
                        whiteList.Add(data.QualifiedItemId);
                    }
                }
                configMenu.AddBoolOption(
                    mod: this.ModManifest,
                    name: () => ItemRegistry.GetData(rawId).DisplayName,
                    getValue: () => !config.blackList.Contains(data.QualifiedItemId),
                    setValue: value =>
                    {
                        if (!value)
                        { config.blackList.Add(data.QualifiedItemId); whiteList.Remove(data.QualifiedItemId); }
                        else
                        { config.blackList.Remove(data.QualifiedItemId); whiteList.Add(data.QualifiedItemId); }
                    }
                );
            }
        }

        static int tickCount = 1;
        public void onUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (--tickCount > 0) return;
            if (!Context.IsWorldReady) reloadGMCM();
            tickCount = config.gmcmInterval;
        }
    }
}