using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.Definitions.Crafting;
using Medieval.Entities.Components.Crafting;
using Meds.Metrics;
using Meds.Shared;
using Microsoft.Extensions.Logging;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Utils;
using Patches = Meds.Wrapper.Shim.Patches;

namespace Meds.Wrapper.Metrics
{
    public class CraftingMetrics
    {
        private static bool _allCraftingCategories;
        private static readonly HashSet<MyStringHash> Categories = new HashSet<MyStringHash>(MyStringHash.Comparer);

        private static bool _allCraftingComponents;
        private static readonly HashSet<MyStringHash> Components = new HashSet<MyStringHash>(MyStringHash.Comparer);


        public static void Register(MetricConfig config)
        {
            _allCraftingCategories = config.AllCraftingCategories;
            if (config.CraftingCategories != null)
                foreach (var category in config.CraftingCategories)
                    Categories.Add(MyStringHash.GetOrCompute(category));

            _allCraftingComponents = config.AllCraftingComponents;
            if (config.CraftingComponents != null)
                foreach (var component in config.CraftingComponents)
                    Components.Add(MyStringHash.GetOrCompute(component));

            if ((_allCraftingCategories || Categories.Count > 0) && (_allCraftingComponents || Components.Count > 0))
            {
                MyCraftingComponent.OnCraftingFinished += CraftingFinished;
            }

            if (config.AuctionHouse)
                Patches.Patch(typeof(PatchAuctionHouse));
        }

        private static bool ShouldTrack(MyCraftingComponent block)
        {
            return _allCraftingComponents || Components.Contains(block.SubtypeId);
        }

        private static bool ShouldTrack(MyCraftingRecipeDefinition definition)
        {
            if (_allCraftingCategories)
                return true;
            foreach (var category in definition.Categories)
                if (Categories.Contains(category))
                    return true;
            return false;
        }

        private static string SubtypeOrDefault(MyDefinitionId id) => id.SubtypeId == MyStringHash.NullOrEmpty ? "default" : id.SubtypeId.String;

        private static void CraftingFinished(MyCraftingComponent craftingComponent, MyCraftingRecipeDefinition recipe, CraftingAuthor? author)
        {
            if (!ShouldTrack(craftingComponent) || !ShouldTrack(recipe))
                return;
            var crafterId = SubtypeOrDefault(craftingComponent.DefinitionId);
            var recipeId = SubtypeOrDefault(recipe.Id);
            var name = MetricName.Of(
                "me.crafting",
                "crafter", crafterId,
                "recipe", recipeId);

            void LogItem(MyDefinitionId item, string type, int delta) =>
                MetricRegistry.Group(name.WithTag("item", SubtypeOrDefault(item))).Counter(type).Inc(delta);

            foreach (var input in recipe.Prerequisites)
                LogItem(input.Id, "input", input.Amount);

            foreach (var output in recipe.Results)
                LogItem(output.Id, "output", output.Amount);

            MetricRegistry.Group(name).Counter("count").Inc();
        }

        [HarmonyPatch]
        private static class PatchAuctionHouse
        {
            private static readonly MethodInfo RemoveItems = AccessTools.Method(typeof(MyInventoryBase), "RemoveItems", new[]
            {
                typeof(MyDefinitionId),
                typeof(int)
            });

            private static readonly MethodInfo AddItems = AccessTools.Method(typeof(MyInventoryBase), "AddItems", new[]
            {
                typeof(MyDefinitionId),
                typeof(int),
                typeof(MyInventoryBase.NewItemParams)
            });

            private static readonly MethodInfo AddItemsShimRef = AccessTools.Method(typeof(PatchAuctionHouse), "AddItemsShim");

            public static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var kv in MySession.Static.ModManager.Assemblies)
                {
                    var type = kv.Value.GetType("Pax.TradingPost.MyPAX_AuctionHouse");
                    if (type == null) continue;
                    var method = AccessTools.Method(type, "PlaceBuyOrder");
                    if (method == null)
                    {
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouse))
                            .LogInformation(
                                "When patching AuctionHouse type from {ModId} ({ModName}) the PlaceBuyOrder method wasn't found",
                                kv.Key.Id, kv.Key.Name);
                        continue;
                    }
                    yield return method;
                }
            }

            private static bool AddItemsShim(
                MyInventoryBase boughtInventory,
                MyDefinitionId boughtItem,
                int boughtAmount,
                MyInventoryBase.NewItemParams boughtArgs,
                MyDefinitionId goldDefinition,
                int goldAmount,
                MyEntityComponent owner)
            {
                var name = MetricName.Of(
                    "me.pax.auctionHouse.bought",
                    "crafter", SubtypeOrDefault(owner.DefinitionId),
                    "item", SubtypeOrDefault(boughtItem),
                    "gold", SubtypeOrDefault(goldDefinition));
                var group = MetricRegistry.Group(in name);
                group.Counter("spent").Inc(goldAmount);
                group.Counter("bought").Inc(boughtAmount);
                return boughtInventory.AddItems(boughtItem, boughtAmount, boughtArgs);
            }

            public static IEnumerable<CodeInstruction> Transpiler(
                MethodBase original,
                IEnumerable<CodeInstruction> instructions,
                ILGenerator generator)
            {
                foreach (var kv in MySession.Static.ModManager.Assemblies)
                    if (original.DeclaringType?.Assembly == kv.Value)
                    {
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouse)).LogInformation(
                            "Trying to patch AuctionHouse type from {ModId} in {Assembly} ({ModName})",
                            kv.Key.Id, kv.Value?.FullName, kv.Key.Name);
                        break;
                    }

                var list = instructions.ToList();
                var goldItemType = generator.DeclareLocal(typeof(MyDefinitionId));
                var goldItemAmount = generator.DeclareLocal(typeof(int));

                var foundGold = false;
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i].Calls(RemoveItems))
                    {
                        list.InsertRange(i, new[]
                        {
                            new CodeInstruction(OpCodes.Stloc, goldItemAmount),
                            new CodeInstruction(OpCodes.Stloc, goldItemType),
                            new CodeInstruction(OpCodes.Ldloc, goldItemType),
                            new CodeInstruction(OpCodes.Ldloc, goldItemAmount),
                        });
                        i += 4;
                        foundGold = true;
                    }
                    else if (list[i].Calls(AddItems))
                    {
                        list[i] = list[i].Clone(AddItemsShimRef).Clone(OpCodes.Call);
                        list.InsertRange(i, new[]
                        {
                            new CodeInstruction(OpCodes.Ldloc, goldItemType),
                            new CodeInstruction(OpCodes.Ldloc, goldItemAmount),
                            new CodeInstruction(OpCodes.Ldarg_0),
                        });
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouse)).LogInformation(
                            "Patched auction house from {Assembly}, {FoundGold}", original.DeclaringType?.Assembly.GetName().Name, foundGold);
                        return list;
                    }
                }

                return list;
            }
        }
    }
}