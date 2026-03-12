using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Linq;
using HarmonyLib;

using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Network;
using StardewValley.Extensions;
using StardewValley.TerrainFeatures;
using StardewValley.GameData;
using StardewValley.GameData.LocationContexts;
using StardewValley.GameData.Objects;

using SObject = StardewValley.Object;
using System.Xml.Linq;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;

namespace BetterStormLightningMod
{
    class ModEntry : Mod
    {
        private static Harmony harmony;
        private static ModConfig config = new ModConfig();
        public const string ModID = "rsjww.BetterStormLightning";
        static double accumulatedTime = 0;
        static int lightningCount = 0;
        private static bool LightningTotemActive = false;
        internal static IModHelper helper;
        public override void Entry(IModHelper init_helper)
        {
            helper = init_helper;
            config = helper.ReadConfig<ModConfig>();

            harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(SObject), nameof(SObject.performUseAction)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(performUseAction_Prefix))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(Utility), nameof(Utility.performLightningUpdate)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(performLightningUpdate_prefix))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(Utility), nameof(Utility.overnightLightning)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(overnightLightning_prefix))
            );
            helper.Events.GameLoop.UpdateTicked += onUpdate;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.Input.ButtonPressed += onButtonDown;
            helper.Events.Content.AssetRequested += onAssetRequested;
        }
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            helper.GameContent.InvalidateCache("Data/Objects");
            helper.GameContent.InvalidateCache("Data/CraftingRecipes");
            // var i = ItemRegistry.Create($"{ModID}.LightningTotem", 10);
            // var j = ItemRegistry.Create($"{ModID}.StormTotem", 10);
            // Game1.player.addItemToInventoryBool(i);
            // Game1.player.addItemToInventoryBool(j);
        }

        private void onButtonDown(object? sender, ButtonPressedEventArgs e)
        {
            if (config.DebugMode)
            {
                if (config.TriggerSmallFlash.IsDown()) SmallFlash();
                if (config.TriggerBigFlash.IsDown()) BigFlash(Game1.random);
            }
        }

        private void onAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;
                    if (config.EnableLightningTotemRecipe)
                        data["Lightning Totem"] = $"(O){ModID}.StormTotem 1/Field/(O){ModID}.LightningTotem/false/Foraging 9/";
                    if (config.EnableStormTotemRecipe)
                        data["Storm Totem"] = $"681 2 (BC)9 2/Field/(O){ModID}.StormTotem/false/Foraging 9/";
                });
            }
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, ObjectData>().Data;
                    data[$"{ModID}.LightningTotem"] = new ObjectData
                    {
                        Name = "Lightning Totem",
                        DisplayName = helper.Translation.Get("lightning_totem.name"),
                        Price = 20,
                        Edibility = -300,
                        Type = "Crafting",
                        Category = 0,
                        Description = helper.Translation.Get("lightning_totem.description"),
                        Texture = $"Mods/{ModID}",
                        SpriteIndex = 1,
                        ExcludeFromRandomSale = true,
                        ContextTags = new List<string> { "color_yellow", "not_placeable", "totem_item" }
                    };
                    data[$"{ModID}.StormTotem"] = new ObjectData
                    {
                        Name = "Storm Totem",
                        DisplayName = helper.Translation.Get("storm_totem.name"),
                        Price = 20,
                        Edibility = -300,
                        Type = "Crafting",
                        Category = 0,
                        Description = helper.Translation.Get("storm_totem.description"),
                        Texture = $"Mods/{ModID}",
                        SpriteIndex = 0,
                        ExcludeFromRandomSale = true,
                        ContextTags = new List<string> { "color_purple", "not_placeable", "totem_item" }
                    };
                });
            }
            if (e.NameWithoutLocale.IsEquivalentTo($"Mods/{ModID}"))
            {
                e.LoadFromModFile<Texture2D>("assets/totem.png", AssetLoadPriority.Medium);
            }

        }
        public static bool performUseAction_Prefix(SObject __instance, ref bool __result, GameLocation location)
        {
            var isTemporarilyInvisible = __instance.isTemporarilyInvisible;
            if (!Game1.player.canMove || isTemporarilyInvisible)
            {
                return true;
            }
            switch (__instance.QualifiedItemId)
            {
                case $"(O){ModID}.LightningTotem":
                    __result = LightningTotem(__instance, Game1.currentGameTime.TotalGameTime.Seconds);
                    return false;
                case $"(O){ModID}.StormTotem":
                    __result = StormTotem(__instance);
                    return false;
            }
            return true;
        }

        public static bool overnightLightning_prefix(int timeWentToSleep)
        {
            // if (Game1.IsMasterGame)
            // The totem individually attracts lightning for each player, so no need to check for MasterGame
            int leftnumber = config.MaxLightningTriggered - lightningCount;
            leftnumber = (int)(0.5f * leftnumber * Game1.random.NextDouble());
            if (LightningTotemActive)
            {
                LightningTotemActive = false;
                for (int i = 0; i < leftnumber; i++) {
                    BigFlash(Game1.random);
                }
            }
            return true;
        }

        private static bool StormTotem(SObject __instance)
        {
            GameLocation location = Game1.player.currentLocation;
            string contextId = location.GetLocationContextId();
            LocationContextData context = location.GetLocationContext();
            if (contextId != "Default" || context == null)
            {
                Game1.showRedMessageUsingLoadString("Strings\\UI:Item_CantBeUsedHere");
                return false;
            }
            if (context.RainTotemAffectsContext != null)
            {
                contextId = context.RainTotemAffectsContext;
            }
            bool applied = false;
            if (contextId == "Default")
            {
                if (!Utility.isFestivalDay(Game1.dayOfMonth + 1, Game1.season))
                {
                    Game1.netWorldState.Value.WeatherForTomorrow = (Game1.weatherForTomorrow = "Storm");
                    applied = true;
                }
            }
            else
            {
                location.GetWeather().WeatherForTomorrow = "Storm";
                applied = true;
            }
            if (applied)
            {
                Game1.pauseThenMessage(2000, Game1.content.LoadString("Strings\\StringsFromCSFiles:Object.cs.12822"));
            }
            else
            {
                Game1.pauseThenMessage(2000, helper.Translation.Get("protected_area_caption"));
            }
            Farmer who = Game1.player;
            Game1.screenGlow = false;
            location.playSound("thunder");
            Game1.player.canMove = false;
            Game1.screenGlowOnce(Color.SlateBlue, hold: false);
            Game1.player.faceDirection(2);
            Game1.player.FarmerSprite.animateOnce(new FarmerSprite.AnimationFrame[1] {
                new FarmerSprite.AnimationFrame(57, 2000, secondaryArm: false, flip: false, Farmer.canMoveNow, behaviorAtEndOfFrame: true)
            });
            for (int i = 0; i < 6; i++)
            {
                Game1.Multiplayer.broadcastSprites(location, new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(648, 1045, 52, 33), 9999f, 1, 999, who.Position + new Vector2(0f, -128f), flicker: false, flipped: false, 1f, 0.01f, Color.White * 0.8f, 2f, 0.01f, 0f, 0f)
                {
                    motion = new Vector2((float)Game1.random.Next(-10, 11) / 10f, -2f),
                    delayBeforeAnimationStart = i * 200
                });
                Game1.Multiplayer.broadcastSprites(location, new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(648, 1045, 52, 33), 9999f, 1, 999, who.Position + new Vector2(0f, -128f), flicker: false, flipped: false, 1f, 0.01f, Color.White * 0.8f, 1f, 0.01f, 0f, 0f)
                {
                    motion = new Vector2((float)Game1.random.Next(-30, -10) / 10f, -1f),
                    delayBeforeAnimationStart = 100 + i * 200
                });
                Game1.Multiplayer.broadcastSprites(location, new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(648, 1045, 52, 33), 9999f, 1, 999, who.Position + new Vector2(0f, -128f), flicker: false, flipped: false, 1f, 0.01f, Color.White * 0.8f, 1f, 0.01f, 0f, 0f)
                {
                    motion = new Vector2((float)Game1.random.Next(10, 30) / 10f, -1f),
                    delayBeforeAnimationStart = 200 + i * 200
                });
            }
            TemporaryAnimatedSprite sprite = new TemporaryAnimatedSprite(0, 9999f, 1, 999, Game1.player.Position + new Vector2(0f, -96f), flicker: false, flipped: false, verticalFlipped: false, 0f)
            {
                motion = new Vector2(0f, -7f),
                acceleration = new Vector2(0f, 0.1f),
                scaleChange = 0.015f,
                alpha = 1f,
                alphaFade = 0.0075f,
                shakeIntensity = 1f,
                initialPosition = Game1.player.Position + new Vector2(0f, -96f),
                xPeriodic = true,
                xPeriodicLoopTime = 1000f,
                xPeriodicRange = 4f,
                layerDepth = 1f
            };
            sprite.CopyAppearanceFromItemId(__instance.QualifiedItemId);
            Game1.Multiplayer.broadcastSprites(location, sprite);
            DelayedAction.playSoundAfterDelay("rainsound", 2000);
            return true;
        }
        private void onUpdate(object sender, EventArgs e)
        {
            if (LightningTotemActive)
            {
                accumulatedTime += Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
                int r1 = config.MaxLightningTriggered / 2, r2 = config.MaxLightningTriggered;
                if (lightningCount <= r1)
                {
                    if (accumulatedTime * 2.0 > lightningCount)
                    {
                        lightningCount++;
                        if (Game1.random.NextDouble() < 0.1) accumulatedTime /= 2.0;
                        BigFlash(Game1.random);
                    }
                }
                else if (r1 < lightningCount && lightningCount <= r2)
                {
                    if (accumulatedTime > lightningCount)
                    {
                        if (Game1.random.NextDouble() < 0.7)
                        {
                            accumulatedTime -= 1;
                        }
                        else
                        {
                            lightningCount++;
                            BigFlash(Game1.random);
                        }
                    }
                }
                else if (lightningCount > r2)
                {
                    LightningTotemActive = false;
                }
            }
        }

        private static void SmallFlash()
        {
            Farm.LightningStrikeEvent lightningEvent2 = new Farm.LightningStrikeEvent();
            lightningEvent2.smallFlash = true;
            Farm farm = Game1.getFarm();
            farm.lightningStrikeEvent.Fire(lightningEvent2);
        }


        public static bool performLightningUpdate_prefix(int time_of_day)
        {
            Random random = Utility.CreateRandom(Game1.uniqueIDForThisGame, Game1.stats.DaysPlayed, time_of_day);
            if (random.NextDouble() < 0.125 + Game1.player.team.AverageDailyLuck() + Game1.player.team.AverageLuckLevel() / 100.0)
            {
                BigFlash(random);
            }
            else if (random.NextDouble() < 0.1)
            {
                SmallFlash();
            }
            return false;
        }


        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null) return;

            configMenu.Register(
                mod: this.ModManifest,
                reset: () => config = new ModConfig(),
                save: () => this.Helper.WriteConfig(config)
            );

            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => helper.Translation.Get("gmcm.unlock_recipe"),
                tooltip: () => helper.Translation.Get("gmcm.unlock_recipe.description")
            );
            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("storm_totem.name"),
                tooltip: () => helper.Translation.Get("gmcm.enable_storm_totem_tooltip"),
                getValue: () => config.EnableStormTotemRecipe,
                setValue: value => config.EnableStormTotemRecipe = value
            );
            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("lightning_totem.name"),
                tooltip: () => helper.Translation.Get("gmcm.enable_lightning_totem_tooltip"),
                getValue: () => config.EnableLightningTotemRecipe,
                setValue: value => config.EnableLightningTotemRecipe = value
            );


            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => helper.Translation.Get("gmcm.lightning_behavior"),
                tooltip: () => helper.Translation.Get("gmcm.lightning_behavior.description")
            );
            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.safe_lightning"),
                tooltip: () => helper.Translation.Get("gmcm.safe_lightning.tooltip"),
                getValue: () => config.SafeLightning,
                setValue: value => config.SafeLightning = value
            );
            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.unsafe_lightning"),
                tooltip: () => helper.Translation.Get("gmcm.unsafe_lightning.tooltip"),
                getValue: () => config.UnsafeLightning,
                setValue: value => config.UnsafeLightning = value
            );

            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => helper.Translation.Get("gmcm.lightning_totem_section"),
                tooltip: () => helper.Translation.Get("gmcm.lightning_totem_section.tooltip")
            );
            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.easteregg_probability"),
                tooltip: () => helper.Translation.Get("gmcm.easteregg_probability.tooltip"),
                getValue: () => (float)config.EasterEggProbability,
                setValue: (float value) => config.EasterEggProbability = value,
                min: 0f,
                max: 1f,
                formatValue: value => value.ToString("0.000"),
                interval: 0.001f
            );
            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.max_lightning"),
                tooltip: () => helper.Translation.Get("gmcm.max_lightning.tooltip"),
                getValue: () => config.MaxLightningTriggered,
                setValue: (int val) => config.MaxLightningTriggered = val,
                min: 0,
                max: 200
            );
            configMenu.AddSectionTitle(
                mod: this.ModManifest,
                text: () => helper.Translation.Get("gmcm.manual_trigger_keys"),
                tooltip: () => helper.Translation.Get("gmcm.manual_trigger_keys.tooltip")
            );

            configMenu.AddBoolOption(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.debug_enable"),
                tooltip: () => helper.Translation.Get("gmcm.debug_enable.tooltip"),
                getValue: () => config.DebugMode,
                setValue: value => config.DebugMode = value
            );

            configMenu.AddKeybindList(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.small_flash"),
                tooltip: () => helper.Translation.Get("gmcm.small_flash.tooltip"),
                getValue: () => config.TriggerSmallFlash,
                setValue: value => config.TriggerSmallFlash = value
            );

            configMenu.AddKeybindList(
                mod: this.ModManifest,
                name: () => helper.Translation.Get("gmcm.big_flash"),
                tooltip: () => helper.Translation.Get("gmcm.big_flash.tooltip"),
                getValue: () => config.TriggerBigFlash,
                setValue: value => config.TriggerBigFlash = value
            );
        }

        private static void BigFlash(Random _random)
        {
            Farm.LightningStrikeEvent lightningEvent = new Farm.LightningStrikeEvent();
            lightningEvent.bigFlash = true;
            Farm farm = Game1.getFarm();
            List<Vector2> lightningRods = new List<Vector2>();
            foreach (KeyValuePair<Vector2, SObject> v in farm.objects.Pairs)
            {
                if (v.Value.QualifiedItemId == "(BC)9")
                {
                    lightningRods.Add(v.Key);
                }
            }
            Random random = _random;
            if (lightningRods.Count > 0)
            {
                for (int i = 0; i < 2; i++)
                {
                    Vector2 v2 = random.ChooseFrom(lightningRods);
                    if (farm.objects[v2].heldObject.Value == null)
                    {
                        farm.objects[v2].heldObject.Value = ItemRegistry.Create<SObject>("(O)787");
                        farm.objects[v2].minutesUntilReady.Value = Utility.CalculateMinutesUntilMorning(Game1.timeOfDay);
                        farm.objects[v2].shakeTimer = 1000;
                        lightningEvent.createBolt = true;
                        lightningEvent.boltPosition = v2 * 64f + new Vector2(32f, -128f);
                        farm.lightningStrikeEvent.Fire(lightningEvent);
                        return;
                    }
                }
            }
            if (!config.SafeLightning && (config.UnsafeLightning || (random.NextDouble() < 0.25 - Game1.player.team.AverageDailyLuck() - Game1.player.team.AverageLuckLevel() / 100.0)))
            {
                try
                {
                    if (Utility.TryGetRandom(farm.terrainFeatures, out var tile, out var feature))
                    {
                        if (feature is FruitTree fruitTree)
                        {
                            fruitTree.struckByLightningCountdown.Value = 4;
                            fruitTree.shake(tile, doEvenIfStillShaking: true);
                            lightningEvent.createBolt = true;
                            lightningEvent.boltPosition = tile * 64f + new Vector2(32f, -128f);
                        }
                        else
                        {
                            Crop crop = (feature as HoeDirt)?.crop;
                            bool num = crop != null && !crop.dead.Value;
                            if (feature.performToolAction(null, 50, tile))
                            {
                                lightningEvent.destroyedTerrainFeature = true;
                                lightningEvent.createBolt = true;
                                farm.terrainFeatures.Remove(tile);
                                lightningEvent.boltPosition = tile * 64f + new Vector2(32f, -128f);
                            }
                            if (num && crop.dead.Value)
                            {
                                lightningEvent.createBolt = true;
                                lightningEvent.boltPosition = tile * 64f + new Vector2(32f, 0f);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // this.Monitor.Log($"Failed to strike lightning: {exce}", LogLevel.Error);
                }
            }
            farm.lightningStrikeEvent.Fire(lightningEvent);
        }

        private static bool LightningTotem(SObject __instance, int time_of_day)
        {

            GameLocation location = Game1.player.currentLocation;
            string contextId = location.GetLocationContextId();
            LocationContextData context = location.GetLocationContext();
            if (!Game1.currentLocation.name.Equals("Farm"))
            {
                Game1.showRedMessage(helper.Translation.Get("farm_only_caption"));
                return false;
            }
            if (!Game1.isRaining && !Game1.isLightning)
            {
                Game1.showRedMessage(helper.Translation.Get("rain_only_caption"));
                return false;
            }
            if (contextId != "Default" || context == null)
            {
                Game1.showRedMessageUsingLoadString("Strings\\UI:Item_CantBeUsedHere");
                return false;
            }
            if (context.RainTotemAffectsContext != null)
            {
                contextId = context.RainTotemAffectsContext;
            }
            LocationWeather weather = Game1.netWorldState.Value.GetWeatherForLocation(contextId);
            weather.IsLightning = true;
            Game1.isLightning = true;
            Game1.pauseThenMessage(2000, helper.Translation.Get("lightning_totem_applied"));

            Game1.screenGlow = false;
            SmallFlash();
            Farmer who = Game1.player;
            // Gamer1.player vs who ??

            who.canMove = false;
            Random random = Utility.CreateRandom(Game1.uniqueIDForThisGame, Game1.stats.DaysPlayed, time_of_day);
            if (random.NextDouble() < config.EasterEggProbability)
            {
                Game1.delayedActions.Add(new DelayedAction(5800, () =>
                    {
                        who.canMove = true;

                        Vector2 v2 = Game1.player.Position;
                        Farm.LightningStrikeEvent lightningEvent = new Farm.LightningStrikeEvent();
                        lightningEvent.bigFlash = true;
                        Farm farm = Game1.getFarm();
                        List<Vector2> lightningRods = new List<Vector2>();
                        lightningEvent.createBolt = true;
                        lightningEvent.boltPosition = v2 + new Vector2(32f, 0f);
                        int damage = who.health / 2;
                        if (Game1.currentLocation.name.Equals("Farm"))
                        {
                            who.canMove = false;
                            farm.lightningStrikeEvent.Fire(lightningEvent);
                            who.takeDamage(damage, overrideParry: true, null);
                            Game1.delayedActions.Add(new DelayedAction(750, () =>
                            {
                                who.canMove = true;
                            }));
                        }
                    }));
            }

            Game1.screenGlowOnce(Color.SlateBlue, hold: false);
            Game1.player.faceDirection(2);
            Game1.player.FarmerSprite.animateOnce(new FarmerSprite.AnimationFrame[1] {
                new FarmerSprite.AnimationFrame(57, 2000, secondaryArm: false, flip: false, Farmer.canMoveNow, behaviorAtEndOfFrame: true)
            });
            for (int i = 0; i < 6; i++)
            {
                Game1.Multiplayer.broadcastSprites(location, new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(648, 1045, 52, 33), 9999f, 1, 999, who.Position + new Vector2(0f, -128f), flicker: false, flipped: false, 1f, 0.01f, Color.White * 0.8f, 2f, 0.01f, 0f, 0f)
                {
                    motion = new Vector2((float)Game1.random.Next(-10, 11) / 10f, -2f),
                    delayBeforeAnimationStart = i * 200
                });
                Game1.Multiplayer.broadcastSprites(location, new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(648, 1045, 52, 33), 9999f, 1, 999, who.Position + new Vector2(0f, -128f), flicker: false, flipped: false, 1f, 0.01f, Color.White * 0.8f, 1f, 0.01f, 0f, 0f)
                {
                    motion = new Vector2((float)Game1.random.Next(-30, -10) / 10f, -1f),
                    delayBeforeAnimationStart = 100 + i * 200
                });
                Game1.Multiplayer.broadcastSprites(location, new TemporaryAnimatedSprite("LooseSprites\\Cursors", new Rectangle(648, 1045, 52, 33), 9999f, 1, 999, who.Position + new Vector2(0f, -128f), flicker: false, flipped: false, 1f, 0.01f, Color.White * 0.8f, 1f, 0.01f, 0f, 0f)
                {
                    motion = new Vector2((float)Game1.random.Next(10, 30) / 10f, -1f),
                    delayBeforeAnimationStart = 200 + i * 200
                });
            }
            TemporaryAnimatedSprite sprite = new TemporaryAnimatedSprite(0, 9999f, 1, 999, Game1.player.Position + new Vector2(0f, -96f), flicker: false, flipped: false, verticalFlipped: false, 0f)
            {
                motion = new Vector2(0f, -7f),
                acceleration = new Vector2(0f, 0.1f),
                scaleChange = 0.015f,
                alpha = 1f,
                alphaFade = 0.0075f,
                shakeIntensity = 1f,
                initialPosition = Game1.player.Position + new Vector2(0f, -96f),
                xPeriodic = true,
                xPeriodicLoopTime = 1000f,
                xPeriodicRange = 4f,
                layerDepth = 1f
            };
            sprite.CopyAppearanceFromItemId(__instance.QualifiedItemId);
            Game1.Multiplayer.broadcastSprites(location, sprite);
            DelayedAction.playSoundAfterDelay("rainsound", 2000);
            Game1.delayedActions.Add(new DelayedAction(6000, () =>
            {
                LightningTotemActive = true;
                lightningCount = 0;
            }));
            return true;
        }
    }

}