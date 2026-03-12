using System.Collections.Generic;
using System.Linq;
using StardewValley.ItemTypeDefinitions;
using StardewValley;
using SObject = StardewValley.Object;

namespace BalancedAutomate
{
    class LegacyBufferItemMigration
    {
        private static SObject? ResetFlavour(SObject artisan, SObject item)
        {
            ObjectDataDefinition objectDataDefinition = (ObjectDataDefinition)ItemRegistry.GetTypeDefinition(ItemRegistry.type_object);
            if (item is null)
                return null;
            string id = artisan.ItemId;

            // fish
            if (id == "SmokedFish")
                return objectDataDefinition.CreateFlavoredSmokedFish(item);
            if (id == "SpecificBait")
                return objectDataDefinition.CreateFlavoredBait(item);

            // fruit products
            if (id == "348") // wine
                return objectDataDefinition.CreateFlavoredWine(item);
            if (id == "344") // jelly
                return objectDataDefinition.CreateFlavoredJelly(item);
            if (id == "398" && item.QualifiedItemId != "(O)398") // dried fruit
                return objectDataDefinition.CreateFlavoredDriedFruit(item);

            // greens
            if (id == "342") // pickle
                return objectDataDefinition.CreateFlavoredPickle(item);
            if (id == "350" && item.Edibility > 0 && !item.HasContextTag("edible_mushroom")) // juice
                return objectDataDefinition.CreateFlavoredJuice(item);

            // vegetable products
            if (id == "350") // juice
                return objectDataDefinition.CreateFlavoredJuice(item);
            if (id == "342") // pickle
                return objectDataDefinition.CreateFlavoredPickle(item);
            // flower honey
            if (id == "340")
                return objectDataDefinition.CreateFlavoredHoney(item);

            if (id == "812" && item.HasContextTag("fish_has_roe")) // roe
                return objectDataDefinition.CreateFlavoredRoe(item);
            if (id == "447" && item.HasContextTag("fish_has_roe") && item.QualifiedItemId != "(O)698")
                return objectDataDefinition.CreateFlavoredAgedRoe(item);
            if (id == "350" && !item.HasContextTag("keg_juice")) // juice fallback
                return objectDataDefinition.CreateFlavoredJuice(item);
            if (id == "342" && item.HasContextTag("preserves_pickle")) // pickle fallback
                return objectDataDefinition.CreateFlavoredPickle(item);
            if (id == "DriedMushrooms" && item.HasContextTag("edible_mushroom"))
                return objectDataDefinition.CreateFlavoredDriedMushroom(item);
            return null;
        }
        public struct BufferItem
        {
            public string QID;
            public int Quality;
            public int Stack;
            public BufferItem(string qid, int quality, int stack) => (QID, Quality, Stack) = (qid, quality, stack);
            public BufferItem(SObject obj)
            {
                if (obj.preservedParentSheetIndex.Value == null || obj.preservedParentSheetIndex.Value == "-1")
                {
                    QID = obj.QualifiedItemId;
                    Quality = obj.Quality;
                    Stack = obj.Stack;
                }
                else
                {
                    QID = obj.QualifiedItemId + "/" + obj.preservedParentSheetIndex.Value;
                    Quality = obj.Quality;
                    Stack = obj.Stack;
                }
            }
            public override string ToString() => $"{QID},{Quality},{Stack}";
            public SObject ToSObject()
            {
                if (QID.Contains("/"))
                {
                    SObject artisan = (SObject)ItemRegistry.Create(QID.Split('/')[0], Stack, Quality);
                    SObject ingredient = (SObject)ItemRegistry.Create(QID.Split('/')[1], Stack, Quality);
                    SObject rev = ResetFlavour(artisan, ingredient) ?? artisan;
                    rev.Stack = Stack;
                    rev.Quality = Quality;
                    return rev;
                }
                else
                {
                    SObject rev = (SObject)ItemRegistry.Create(QID, Stack, Quality);
                    return rev;
                }
            }

        };
        static public List<BufferItem> _ReadBuffer(SObject obj)
        {
            if (!obj.modData.TryGetValue($"{ModEntry.ModID}.ready", out var s) || string.IsNullOrEmpty(s)) return new();
            return s.Split(';').Select(e => { var p = e.Split(','); return new BufferItem(p[0], int.Parse(p[1]), int.Parse(p[2])); }).ToList();
        }
        static public bool _EmptyBuffer(SObject obj) => !obj.modData.TryGetValue($"{ModEntry.ModID}.ready", out var s) || string.IsNullOrEmpty(s);
        
    }
}