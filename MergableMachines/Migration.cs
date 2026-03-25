using MergableMachines;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
namespace MergableMachines
{
    public class LegacyMigration
    {
        public static void checkAll()
        {
            IEnumerable<GameLocation> all_locations = Game1.locations.Concat(
               from location in Game1.locations
               from indoors in location.GetInstancedBuildingInteriors()
               select indoors
           );
            foreach (var loc in all_locations)
                foreach (var obj in loc.Objects.Values)
                    checkOne(obj);
        }
        public static void checkOne(StardewValley.Object obj)
        {
            if(obj.modData.ContainsKey("rsjww.MergableMachines.MyMachine")) return;
            if (obj.Stack > 1 && ModEntry.whiteList.Contains(obj.QualifiedItemId))
                obj.modData["rsjww.MergableMachines.MyMachine"] = "1";
            else
                obj.modData["rsjww.MergableMachines.MyMachine"] = "0";
        }
    }
}