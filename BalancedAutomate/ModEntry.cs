using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.Objects;
using StardewValley.Logging;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Delegates;
using StardewValley.Internal;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;
using Object = System.Object;

namespace BalancedAutomate
{
    public class ModEntry : Mod
    {
        #region AutoStackHelper
        static string? GetInventoryID(SObject obj, bool probe = false)
        {
            string _key = $"{ModID}.inventory";
            if (obj.modData.TryGetValue(_key, out var s) && !string.IsNullOrEmpty(s)) return s;
            if (probe) return null;
            string id = $"{ModID}.temp.{Guid.NewGuid().ToString("N")}";
            obj.modData[_key] = id;
            return id;
        }

        static void _MigrationToInventory(SObject obj)
        {
            if (!LegacyBufferItemMigration._EmptyBuffer(obj))
            {
                foreach (var i in LegacyBufferItemMigration._ReadBuffer(obj))
                {
                    AddToTempInventory(obj, i.ToSObject());
                    monitor.Log($"Migrating {i.QID} in machine {obj.Name} to new: temp inventory: {GetInventoryID(obj)}");
                }
                obj.modData.Remove($"{ModID}.ready");
            }
        }
        static bool AddToTempInventory(SObject obj, SObject item, bool probe = false)
        {
            if (item == null)
            {
                monitor.Log($"{obj.Name} at {obj.Location}:{obj.tileLocation.Value} has nothing to output. This is unexpected.", LogLevel.Warn);
                return false;
            }
            IInventory inventory = Game1.player.team.GetOrCreateGlobalInventory(GetInventoryID(obj));
            bool Full = (inventory.Count >= config.maxSlot);
            foreach (Item slot in inventory)
            {
                if (!slot.canStackWith(item)) continue;
                if (slot.Stack + item.Stack > slot.maximumStackSize() && Full) return false;
                if (probe) return true;
                item.Stack = slot.addToStack(item);
                item.onDetachedFromParent();
                if (item.Stack > 0) inventory.Add(item);
                return true;
            }
            if (Full) return false;
            if (probe) return true;
            item.onDetachedFromParent();
            inventory.Add(item);
            return true;
        }

        // Client player can call this too! Take care!
        static SObject? GetFirstOrNull(SObject obj, bool forceRemove = false)
        {
            string? id = GetInventoryID(obj, probe: true);
            if (id is null) return null;
            if (!Game1.player.team.globalInventories.TryGetValue(id, out var inventory)) return null;
            if (inventory.Count == 0) return null;
            SObject rev = (SObject)inventory[0];
            if (forceRemove)
            {
                inventory.RemoveAt(0);
                inventory.RemoveEmptySlots();
            }
            return rev;
        }

        #endregion

        static Harmony harmony = null!;
        static ModConfig config = new ModConfig();
        static IModHelper helper = null!;
        static public IMonitor monitor = null!;
        public static string ModID = null!;
        static HashSet<string> whiteList = new HashSet<string>();

        public override void Entry(IModHelper init_helper)
        {
            ModID = ModManifest.UniqueID;
            helper = init_helper;
            monitor = this.Monitor;
            harmony = new Harmony(ModID);
            config = helper.ReadConfig<ModConfig>();


            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), "CheckForActionOnMachine"),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(CheckForActionOnMachine_Prefix)) { priority = HarmonyLib.Priority.Last }
            ); // This patches output SObject from heldObject to ModData
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.minutesElapsed)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(minutesElapsed_Prefix)) { priority = HarmonyLib.Priority.Last } // make sure to block original method only, let other prefixes run
            ); // This patches overnight logic, machine will reload when sleeping!
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.placementAction)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(placementAction_Prefix)) { priority = HarmonyLib.Priority.Last }
            ); // safe check. maybe it's not needed
            // These patches break compatibility TAT
            // but they are needed to make the mod work 

            if (Helper.ModRegistry.IsLoaded("PeacefulEnd.AlternativeTextures"))
            { // Compatibility Patch
                monitor.Log("PeacefulEnd.AlternativeTextures detected, re-patching Object.draw", LogLevel.Info);
                AlternativeTexturesPatch.Apply(harmony);
            }
            if (Helper.ModRegistry.IsLoaded("DaLion.Core"))
            { // Compatibility Patch
                monitor.Log("DaLion.Core detected, blocking ObjectMinutesElapsedPatcher", LogLevel.Info);
                DaLionheartPatch.Apply(harmony);
            }
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), new Type[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                transpiler: new HarmonyMethod(typeof(ModEntry), nameof(draw_Transpiler)) { priority = HarmonyLib.Priority.First }
            ); // This adds drawing of ModData, also handles the logic of Auto-Stacking
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.draw), new Type[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(draw_Postfix)) { priority = HarmonyLib.Priority.First }
            ); // This adds drawing of ModData, also handles the logic of Auto-Stacking

            helper.Events.GameLoop.UpdateTicked += onUpdateTicked;
            helper.Events.Content.AssetReady += onAssetReady;
            helper.Events.GameLoop.Saving += onSaving;
            helper.Events.GameLoop.SaveLoaded += onSaveLoaded;
            helper.Events.Content.AssetRequested += onAssetRequested;
            helper.Events.GameLoop.GameLaunched += (s, e) => reloadGMCM();
        }

        public void GarbageCollect()
        {
            if (!Context.IsMainPlayer) return;
            HashSet<string> liveIds = new();
            IEnumerable<GameLocation> all_locations = Game1.locations.Concat(
                from location in Game1.locations
                from indoors in location.GetInstancedBuildingInteriors()
                select indoors
            );
            foreach (var loc in all_locations)
                foreach (var obj in loc.Objects.Values)
                    if (obj.modData.TryGetValue($"{ModID}.inventory", out var id))
                        liveIds.Add(id);
            var toRemove = new List<string>();
            string prefix = $"{ModID}.temp";
            foreach (var id in Game1.player.team.globalInventories.Keys)
                if (!liveIds.Contains(id) && id.StartsWith(prefix, StringComparison.Ordinal))
                    toRemove.Add(id);
            foreach (var id in toRemove)
            {
                monitor.Log($"GC: Removing {id} from globalInventories");
                foreach (var obj in Game1.player.team.globalInventories[id])
                {
                    if (Game1.player.team.globalInventories.TryGetValue(id, out var inventory))
                    {
                        monitor.Log($"GC:     {id} contains {inventory.Count} items:");
                        foreach (var item in inventory)
                        {
                            monitor.Log($"GC:         {item.Name} x {item.Stack}");
                        }
                    }
                    else
                    {
                        monitor.Log($"GC:         null");
                    }
                }
                Game1.player.team.globalInventories.Remove(id);
            }
        }

        public void onSaving(object? sender, SavingEventArgs e)
        {
            if (config.tellQi)
            {
                if (!Game1.player.mailReceived.Contains($"{ModID}.QiHopperGift"))
                {
                    Game1.addMail($"{ModID}.QiHopperGift");
                    Game1.player.mailReceived.Add($"{ModID}.QiHopperGift");
                }
            }
            if (Context.IsMainPlayer) GarbageCollect();
        }

        public void onSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            helper.GameContent.InvalidateCache("Data/Mail"); // Translation upd
            if (!Context.IsMainPlayer) return;
            IEnumerable<GameLocation> all_locations = Game1.locations.Concat(
                from location in Game1.locations
                from indoors in location.GetInstancedBuildingInteriors()
                select indoors
            );
            foreach (var loc in all_locations)
                foreach (var obj in loc.Objects.Values)
                    _MigrationToInventory(obj);
        }

        public void onAssetReady(object? sender, AssetReadyEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Machines"))
            {
                reloadGMCM();
            }
        }
        public void onAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            string G(string key) => helper.Translation.Get(key);
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Mail"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    data[$"{ModID}.QiHopperGift"] = G("qimail") + "%item id (BC)275 %% [#]" + G("qimail.name");
                });
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
            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => G("gmcm.stack_interval"),
                tooltip: () => G("gmcm.stack_interval.tooltip"),
                getValue: () => config.stackInterval,
                setValue: (int val) => config.stackInterval = val,
                min: 5,
                max: 600,
                interval: 1
            );
            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => G("gmcm.max_slot"),
                tooltip: () => G("gmcm.max_slot.tooltip"),
                getValue: () => config.maxSlot,
                setValue: (int val) => config.maxSlot = val,
                min: 0,
                max: 12,
                interval: 1
            );
            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => G("gmcm.tell_qi"),
                tooltip: () => G("gmcm.tell_qi.tooltip"),
                getValue: () => config.tellQi,
                setValue: value => config.tellQi = value
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
                        {
                            config.blackList.Add(data.QualifiedItemId);
                            whiteList.Remove(data.QualifiedItemId);
                        }
                        else
                        {
                            config.blackList.Remove(data.QualifiedItemId);
                            whiteList.Add(data.QualifiedItemId);
                        }
                    }
                );
            }
        }

        void AutomateStackForAll()
        {
            void ProcessLocation(GameLocation loc)
            {
                foreach (SObject obj in loc.Objects.Values)
                {
                    if (!obj.readyForHarvest.Value)
                        continue;
                    if (obj.heldObject.Value == null)
                        continue;
                    TryAutoStack(obj);
                }
            }
            foreach (GameLocation location in Game1.locations)
            {
                ProcessLocation(location);
                foreach (GameLocation indoors in location.GetInstancedBuildingInteriors()) ProcessLocation(indoors);
            }
        }

        public static bool TryAutoStack(SObject obj)
        {
            if (!Context.IsMainPlayer) return false;
            if (!whiteList.Contains(obj.QualifiedItemId)) return false;
            if (!AddToTempInventory(obj, obj.heldObject.Value, probe: true)) return false;
            return _CheckForActionOnMachine(obj, Game1.player, justCheckingForActivity: false, isAutoStack: true);
        }


        static int tickCount = 1;
        public void onUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (--tickCount > 0) return;
            tickCount = config.stackInterval;
            if (!Context.IsWorldReady) { reloadGMCM(); return; }
            if (Context.IsMainPlayer) AutomateStackForAll();
        }

        #region Patches

        public static bool placementAction_Prefix(SObject __instance, ref bool __result, GameLocation location, int x, int y, Farmer who = null)
        {
            if (__instance.modData.TryGetValue($"{ModID}.inventory", out var id))
            {
                if (Context.IsMainPlayer)
                {
                    monitor.Log($"There's {__instance.Name} at {__instance.TileLocation} with TempInventory data when placed. Clearing up.", LogLevel.Warn);
                    __instance.modData.Remove($"{ModID}.inventory");
                }
            }
            return true;
        }

        // this is SO UGLY and unelegant. what can i say
        public static bool minutesElapsed_Prefix(SObject __instance, ref bool __result, int minutes)
        {
            var heldObject = __instance.heldObject;
            var minutesUntilReady = __instance.minutesUntilReady;
            var readyForHarvest = __instance.readyForHarvest;
            var showNextIndex = __instance.showNextIndex;
            MachineData GetMachineData() => __instance.GetMachineData();
            // !! make sure these are refs, not values !!!
            bool IsSprinkler() => __instance.IsSprinkler();
            void addWorkingAnimation() => __instance.addWorkingAnimation();
            bool ShouldTimePassForMachine() => __instance.ShouldTimePassForMachine();
            GameLocation environment = __instance.Location;
            __result = false;
            if (environment == null) return false;
            if (heldObject.Value != null && __instance.QualifiedItemId != "(BC)165")
            {
                if (IsSprinkler()) return false;
                MachineData machineData = GetMachineData();
                if (Game1.IsMasterGame && (machineData == null || ShouldTimePassForMachine()))
                {
                    if (Game1.newDaySync.hasInstance() && Context.IsMainPlayer) // Patched overnight logic
                    {
                        while (minutesUntilReady.Value < minutes && heldObject.Value != null)
                        {
                            minutes -= minutesUntilReady.Value;
                            minutesUntilReady.Value = 0;
                            readyForHarvest.Value = true;
                            __instance.onReadyForHarvest(); // not sure if this is safe, possibly hooked by other mods
                            showNextIndex.Value = machineData?.ShowNextIndexWhenReady ?? false;
                            if (__instance.lightSource != null)
                            {
                                environment.removeLightSource(__instance.lightSource.Id);
                                __instance.lightSource = null;
                            }
                            if (!TryAutoStack(__instance)) break; // This is beacuse buffer is full
                        }
                    }
                    minutesUntilReady.Value -= minutes;
                }
                if (heldObject.Value == null)
                {
                    return false;
                }
                if (__instance.MinutesUntilReady <= 0 && (machineData == null || !machineData.OnlyCompleteOvernight || Game1.newDaySync.hasInstance()))
                {
                    if (!readyForHarvest.Value && (!Game1.newDaySync.hasInstance() || Game1.newDaySync.hasFinished()))
                    {
                        environment.playSound("dwop");
                    }
                    readyForHarvest.Value = true;
                    minutesUntilReady.Value = 0;
                    __instance.onReadyForHarvest();
                    showNextIndex.Value = machineData?.ShowNextIndexWhenReady ?? false;
                    if (__instance.lightSource != null)
                    {
                        environment.removeLightSource(__instance.lightSource.Id);
                        __instance.lightSource = null;
                    }
                    TryAutoStack(__instance);
                }
                if (machineData != null)
                {
                    if (!readyForHarvest.Value && machineData.WorkingEffects != null && Game1.random.NextDouble() < (double)machineData.WorkingEffectChance)
                    {
                        addWorkingAnimation();
                    }
                }
                else if (!readyForHarvest.Value && Game1.random.NextDouble() < 0.33)
                {
                    addWorkingAnimation();
                }
                return false;
            }
            return true; // run original method "else" part
        }

        public static bool CheckForActionOnMachine_Prefix(SObject __instance, ref bool __result, Farmer who, bool justCheckingForActivity = false)
        {
            __result = _CheckForActionOnMachine(__instance, who, justCheckingForActivity, isAutoStack: false);
            return false;
        }
        public static bool _CheckForActionOnMachine(SObject __instance, Farmer who, bool justCheckingForActivity = false, bool isAutoStack = false)
        {
            GameLocation location = __instance.Location;
            var readyForHarvest = __instance.readyForHarvest;
            var heldObject = __instance.heldObject;
            var lastOutputRuleId = __instance.lastOutputRuleId;
            var lastInputItem = __instance.lastInputItem;
            var showNextIndex = __instance.showNextIndex;
            var tileLocation = __instance.tileLocation;

            bool IsTapper() => __instance.IsTapper();
            void ResetParentSheetIndex() => __instance.ResetParentSheetIndex();
            MachineData GetMachineData() => __instance.GetMachineData();

            if (isAutoStack)
            { // Auto-Stack BEGIN
                if (!Context.IsMainPlayer)
                    return false;
                if (!readyForHarvest.Value || heldObject.Value == null)
                {
                    monitor.Log($"{__instance.Name} at {location}:{__instance.tileLocation.Value} is not ready to harvest. This is unexpected.", LogLevel.Warn);
                    return false;
                }
                MachineData machineData = GetMachineData();
                SObject outputObj = heldObject.Value;
                if (lastOutputRuleId.Value != null)
                {
                    MachineOutputRule outputRule = machineData.OutputRules?.FirstOrDefault((MachineOutputRule p) => p.Id == lastOutputRuleId.Value);
                    if (outputRule != null && outputRule.RecalculateOnCollect)
                    {
                        heldObject.Value = null;
                        __instance.OutputMachine(machineData, outputRule, lastInputItem.Value, who, location, probe: false, heldObjectOnly: true);
                        if (heldObject.Value != null) outputObj = heldObject.Value;
                        else heldObject.Value = outputObj;
                    }
                }
                if (justCheckingForActivity)
                    return AddToTempInventory(__instance, outputObj, probe: true);
                bool checkForReload = false;
                if (who.IsLocalPlayer)
                {
                    heldObject.Value = null;
                    if (!AddToTempInventory(__instance, outputObj))
                    {
                        heldObject.Value = outputObj;
                        return false;
                    }
                    checkForReload = true;
                    MachineDataUtility.UpdateStats(machineData?.StatsToIncrementWhenHarvested, outputObj, outputObj.Stack);
                }

                heldObject.Value = null;
                readyForHarvest.Value = false;
                showNextIndex.Value = false;
                ResetParentSheetIndex();
                if (MachineDataUtility.TryGetMachineOutputRule(__instance, machineData, MachineOutputTrigger.OutputCollected, outputObj.getOne(), who, location, out var outputCollectedRule, out var _, out var _, out var _))
                {
                    __instance.OutputMachine(machineData, outputCollectedRule, lastInputItem.Value, who, location, probe: false);
                }
                if (IsTapper() && location.terrainFeatures.TryGetValue(tileLocation.Value, out var terrainFeature) && terrainFeature is Tree tree)
                {
                    tree.UpdateTapperProduct(__instance, outputObj);
                }
                if (checkForReload)
                {
                    bool SlightlyUnsafeAutoLoad(Farmer who)
                    {
                        GameLocation location = __instance.Location;
                        if (location != null && location.objects.TryGetValue(new Vector2(__instance.TileLocation.X, __instance.TileLocation.Y - 1f), out var fromObj))
                        {
                            Chest chest = fromObj as Chest;
                            if (chest != null && chest.specialChestType.Value == Chest.SpecialChestTypes.AutoLoader)
                            {
                                if (chest.GetMutex().IsLocked()) return false;
                                return __instance.AttemptAutoLoad(chest.Items, who);
                            }
                        }
                        return false;
                    }
                    SlightlyUnsafeAutoLoad(who);
                }
                return true;
            } // Auto-Stack END

            if (GetFirstOrNull(__instance) is SObject outputFromBuffer)
            {
                if (justCheckingForActivity) return true;
                if (who.isMoving()) Game1.haltAfterCheck = false;
                if (!who.addItemToInventoryBool(outputFromBuffer))
                {
                    Game1.showRedMessage(Game1.content.LoadString("Strings\\StringsFromCSFiles:Crop.cs.588"));
                    return false;
                }
                GetFirstOrNull(__instance, forceRemove: true);
                Game1.playSound("coin");
                MachineData machineData = GetMachineData();
                if (machineData != null && machineData.ExperienceGainOnHarvest != null)
                {
                    string[] expSplit = machineData.ExperienceGainOnHarvest.Split(' ');
                    for (int i = 0; i < expSplit.Length; i += 2)
                    {
                        int skill = Farmer.getSkillNumberFromName(expSplit[i]);
                        if (skill != -1 && ArgUtility.TryGetInt(expSplit, i + 1, out var amount, out var _, "int amount"))
                        {
                            who.gainExperience(skill, amount);
                        }
                    }
                }
                return true;
            }

            if (readyForHarvest.Value)
            {
                if (justCheckingForActivity) return true;
                if (who.isMoving()) Game1.haltAfterCheck = false;
                MachineData machineData = GetMachineData();
                SObject outputObj = heldObject.Value;
                if (lastOutputRuleId.Value != null)
                {
                    MachineOutputRule outputRule = machineData.OutputRules?.FirstOrDefault((MachineOutputRule p) => p.Id == lastOutputRuleId.Value);
                    if (outputRule != null && outputRule.RecalculateOnCollect)
                    {
                        heldObject.Value = null;
                        __instance.OutputMachine(machineData, outputRule, lastInputItem.Value, who, location, probe: false, heldObjectOnly: true);
                        if (heldObject.Value != null) outputObj = heldObject.Value;
                        else heldObject.Value = outputObj;
                    }
                }
                bool checkForReload = false;
                if (who.IsLocalPlayer)
                {
                    heldObject.Value = null;
                    if (!who.addItemToInventoryBool(outputObj))
                    {
                        heldObject.Value = outputObj;
                        Game1.showRedMessage(Game1.content.LoadString("Strings\\StringsFromCSFiles:Crop.cs.588"));
                        return false;
                    }
                    Game1.playSound("coin");
                    checkForReload = true;
                    MachineDataUtility.UpdateStats(machineData?.StatsToIncrementWhenHarvested, outputObj, outputObj.Stack);
                }
                heldObject.Value = null;
                readyForHarvest.Value = false;
                showNextIndex.Value = false;
                ResetParentSheetIndex();
                if (MachineDataUtility.TryGetMachineOutputRule(__instance, machineData, MachineOutputTrigger.OutputCollected, outputObj.getOne(), who, location, out var outputCollectedRule, out var _, out var _, out var _))
                {
                    __instance.OutputMachine(machineData, outputCollectedRule, lastInputItem.Value, who, location, probe: false);
                }
                if (IsTapper() && location.terrainFeatures.TryGetValue(tileLocation.Value, out var terrainFeature) && terrainFeature is Tree tree)
                {
                    tree.UpdateTapperProduct(__instance, outputObj);
                }
                if (machineData != null && machineData.ExperienceGainOnHarvest != null)
                {
                    string[] expSplit = machineData.ExperienceGainOnHarvest.Split(' ');
                    for (int i = 0; i < expSplit.Length; i += 2)
                    {
                        int skill = Farmer.getSkillNumberFromName(expSplit[i]);
                        if (skill != -1 && ArgUtility.TryGetInt(expSplit, i + 1, out var amount, out var _, "int amount"))
                        {
                            who.gainExperience(skill, amount);
                        }
                    }
                }
                if (checkForReload)
                {
                    __instance.AttemptAutoLoad(who);
                }
                return true;
            }
            MachineData machineData2 = GetMachineData();
            if (machineData2 != null && machineData2.InteractMethod != null)
            {
                if (StaticDelegateBuilder.TryCreateDelegate<MachineInteractDelegate>(machineData2.InteractMethod, out var method, out var error2))
                {
                    if (!justCheckingForActivity)
                    {
                        return method(__instance, location, who);
                    }
                    return true;
                }
                IGameLogger gamelog = AccessTools.Field(typeof(Game1), "log").GetValue(__instance) as IGameLogger;
                gamelog.Warn($"Machine {__instance.ItemId} has invalid interaction method '{machineData2.InteractMethod}': {error2}");
            }
            return false;
        }
        public static void draw_Postfix(SObject __instance, SpriteBatch spriteBatch, int x, int y, float alpha = 1f)
        {
            var heldObject = __instance.heldObject;
            SObject HarvestObject;
            if (GetFirstOrNull(__instance) is SObject outputFromBuffer)
            {
                HarvestObject = outputFromBuffer;
            }
            else
            {
                if (heldObject.Value != null && __instance.readyForHarvest.Value) HarvestObject = heldObject.Value;
                else return;
            }
            float base_sort = (float)((y + 1) * 64) / 10000f + __instance.tileLocation.X / 50000f;
            if (__instance.IsTapper() || __instance.QualifiedItemId.Equals("(BC)MushroomLog"))
            {
                base_sort += 0.02f;
            }
            float yOffset = 4f * (float)Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 250.0), 2);
            spriteBatch.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 - 8, (float)(y * 64 - 96 - 16) + yOffset)), new Rectangle(141, 465, 20, 24), Color.White * 0.75f, 0f, Vector2.Zero, 4f, SpriteEffects.None, base_sort + 1E-06f);
            ParsedItemData heldItemData2 = ItemRegistry.GetDataOrErrorItem(HarvestObject.QualifiedItemId);
            Texture2D texture3 = heldItemData2.GetTexture();
            if (HarvestObject is ColoredObject coloredObj)
            {
                coloredObj.drawInMenu(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, (float)(y * 64) - 96f - 8f + yOffset)), 1f, 0.75f, base_sort + 1.1E-05f);
                return;
            }
            spriteBatch.Draw(texture3, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64 + 32, (float)(y * 64 - 64 - 8) + yOffset)), heldItemData2.GetSourceRect(), Color.White * 0.75f, 0f, new Vector2(8f, 8f), 4f, SpriteEffects.None, base_sort + 1E-05f);
            if (HarvestObject.Stack > 1)
            {
                HarvestObject.DrawMenuIcons(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, (float)(y * 64 - 64 - 32) + yOffset - 4f)), 1f, 1f, base_sort + 1.2E-05f, StackDrawType.Draw, Color.White);
            }
            else if (HarvestObject.Quality > 0)
            {
                HarvestObject.DrawMenuIcons(spriteBatch, Game1.GlobalToLocal(Game1.viewport, new Vector2(x * 64, (float)(y * 64 - 64 - 32) + yOffset - 4f)), 1f, 1f, base_sort + 1.2E-05f, StackDrawType.HideButShowQuality, Color.White);
            }
        }

        static IEnumerable<CodeInstruction> draw_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 1 < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldarg_0 &&
                    codes[i + 1].opcode == OpCodes.Ldfld &&
                    codes[i + 1].operand is System.Reflection.FieldInfo fi && fi.Name == "readyForHarvest")
                {
                    codes[i].opcode = OpCodes.Ret;
                    return codes;
                }
            }
            monitor.Log("Failed to patch with Transpiler. Visual error may occur.", LogLevel.Error);
            return codes;
        }
        #endregion
    }
}