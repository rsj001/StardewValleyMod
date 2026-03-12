using System;

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Extensions;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;

using SObject = StardewValley.Object;
namespace TruffleCrabSpawn
{

    public sealed class ModConfig
    {
        public float CrabSpawnProbability { get; set; } = 0.002f;
    }
    public interface IGenericModConfigMenuApi
    {
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
        void AddNumberOption(IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name, Func<string> tooltip = null, float? min = null, float? max = null, float? interval = null, Func<float, string> formatValue = null, string fieldId = null);
    }

    public class ModEntry : Mod
    {
        static Harmony harmony;
        private static ModConfig config;
        public override void Entry(IModHelper helper)
        {
            config = helper.ReadConfig<ModConfig>();
            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(FarmAnimal), nameof(FarmAnimal.DigUpProduce)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(DigUpProduce_Prefix))
            );
            helper.Events.GameLoop.GameLaunched += onGameLaunched;
        }
        public void onGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var configMenu = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null) return;

            configMenu.Register(
                mod: this.ModManifest,
                reset: () => config = new ModConfig(),
                save: () => this.Helper.WriteConfig(config)
            );
            configMenu.AddNumberOption(
                mod: this.ModManifest,
                name: () => "Truffle Crab Spawn Probability",
                getValue: () => (float)config.CrabSpawnProbability,
                setValue: (float value) => config.CrabSpawnProbability = value,
                min: 0f,
                max: 1f,
                formatValue: value => value.ToString("0.000"),
                interval: 0.001f
            );
        }
        public static bool DigUpProduce_Prefix(FarmAnimal __instance, GameLocation location, SObject produce)
        {
            Random r = Utility.CreateRandom((double)__instance.myID.Value / 2.0, Game1.stats.DaysPlayed, Game1.timeOfDay);
            bool success = false;
            if (produce.QualifiedItemId == "(O)430" && r.NextDouble() < config.CrabSpawnProbability)
            {
                RockCrab crab = new RockCrab(__instance.Tile, "Truffle Crab");
                Vector2 v = Utility.recursiveFindOpenTileForCharacter(crab, location, __instance.Tile, 50, allowOffMap: false);
                if (v != Vector2.Zero)
                {
                    crab.setTileLocation(v);
                    location.addCharacter(crab);
                    success = true;
                }
            }
            if (!success && Utility.spawnObjectAround(Utility.getTranslatedVector2(__instance.Tile, __instance.FacingDirection, 1f), produce, __instance.currentLocation) && produce.QualifiedItemId == "(O)430")
            {
                Game1.stats.TrufflesFound++;
            }
            if (!r.NextBool((double)__instance.friendshipTowardFarmer.Value / 1500.0))
            {
                __instance.currentProduce.Value = null;
            }
            // Block the original method
            return false;
        }
    }
}