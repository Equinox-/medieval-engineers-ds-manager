using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.Definitions.Crafting;
using Medieval.Entities.Components.Crafting;
using Medieval.GameSystems;
using Meds.Metrics;
using Meds.Shared;
using Microsoft.Extensions.Logging;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Components.Session;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRage.Utils;
using VRageMath;
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
                Patches.Patch(typeof(PatchAuctionHouseBuy));
            Patches.Patch(typeof(PatchAuctionHouseSell));
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

        private const string AuctionHouseType = "Pax.TradingPost.MyPAX_AuctionHouse";

        [HarmonyPatch]
        private static class PatchAuctionHouseBuy
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

            private static readonly MethodInfo AddItemsShimRef = AccessTools.Method(typeof(PatchAuctionHouseBuy), "AddItemsShim");

            public static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var (mod, type) in Patches.ModTypes(AuctionHouseType))
                {
                    var method = AccessTools.Method(type, "PlaceBuyOrder");
                    if (method == null)
                    {
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy))
                            .LogInformation(
                                "When patching AuctionHouse type from {ModId} ({ModName}) the PlaceBuyOrder method wasn't found",
                                mod.Id, mod.Name);
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
                    "me.pax.auctionHouse",
                    "crafter", SubtypeOrDefault(owner.DefinitionId),
                    "item", SubtypeOrDefault(boughtItem),
                    "gold", SubtypeOrDefault(goldDefinition));
                var group = MetricRegistry.Group(in name);
                group.Counter("spent").Inc(goldAmount);
                group.Counter("bought").Inc(boughtAmount);
                group.Counter("count").Inc();
                return boughtInventory.AddItems(boughtItem, boughtAmount, boughtArgs);
            }

            public static IEnumerable<CodeInstruction> Transpiler(
                MethodBase original,
                IEnumerable<CodeInstruction> instructions,
                ILGenerator generator)
            {
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
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy)).LogInformation(
                            "Patched buy order for auction house from {Assembly}, {FoundGold}", original.DeclaringType?.Assembly.GetName().Name, foundGold);
                        return list;
                    }
                }

                return list;
            }
        }

        [HarmonyPatch]
        private static class PatchAuctionHouseSell
        {
            private static readonly MethodInfo RaiseEventShimRef = AccessTools.Method(typeof(PatchAuctionHouseSell), "RaiseEventShim");

            public static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var (mod, type) in Patches.ModTypes(AuctionHouseType))
                {
                    var method = AccessTools.Method(type, "PlaceSellOrder");
                    if (method == null)
                    {
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy))
                            .LogInformation(
                                "When patching AuctionHouse type from {ModId} ({ModName}) the PlaceSellOrder method wasn't found",
                                mod.Id, mod.Name);
                        continue;
                    }

                    yield return method;
                }
            }

            private static void RaiseEventShim<T>(
                IMyMultiplayer multiplayer,
                T auctionHouseInstance,
                Func<T, Action<int, int, ulong, string, string>> method,
                int cost,
                int amount,
                ulong sellerId,
                string itemName,
                string sellerName,
                EndpointId endpoint
            ) where T : MyEntityComponent, IMyEventOwner
            {
                multiplayer.RaiseEvent(auctionHouseInstance, method, cost, amount, sellerId, itemName, sellerName, endpoint);

                var displayNameMethod = AccessTools.Method(auctionHouseInstance.GetType(), "GetDisplayNameItem");
                var definitionField = AccessTools.Field(auctionHouseInstance.GetType(), "m_definition");

                var displayName = displayNameMethod?.Invoke(auctionHouseInstance, new object[] { itemName }) ?? itemName;
                var definition = definitionField?.GetValue(auctionHouseInstance) as MyDefinitionBase;

                var currency = "Money";
                if (definition != null)
                {
                    var nameProperty = AccessTools.PropertyGetter(definition.GetType(), "CurrencyName");
                    var currencyName = nameProperty?.Invoke(definition, Array.Empty<object>());
                    if (currencyName is string str)
                        currency = str;

                    var hideSellerProperty = AccessTools.PropertyGetter(definition.GetType(), "HideSellersName");
                    if (hideSellerProperty != null && hideSellerProperty.Invoke(definition, Array.Empty<object>()) is bool hidden && hidden)
                        sellerName = "Anonymous";
                }

                var position = auctionHouseInstance.Entity?.PositionComp.WorldAABB.Center;
                var location = "Unknown";
                if (position.HasValue)
                {
                    var planet = MyGamePruningStructureSandbox.GetClosestPlanet(position.Value);
                    var area = planet?.Get<MyPlanetAreasComponent>()?
                        .GetArea(Vector3D.Transform(position.Value, planet.PositionComp.WorldMatrixNormalizedInv));
                    if (area.HasValue)
                    {
                        MyPlanetAreasComponent.UnpackAreaId(area.Value, out string kingdomName, out var regionName, out var areaName);
                        location = $"{kingdomName}, {regionName}, {areaName}";
                    }
                }

                using var builder = MedsModApi.SendModEvent("pax.auctionHouse.selling", definition?.Package);
                builder.SetEmbedTitle("New Auction House Offer");
                builder.AddInlineField("Selling", $"{amount}x {displayName}");
                builder.AddInlineField("Price", $"{cost}x {currency}");
                builder.AddInlineField("Seller", sellerName);
                builder.AddInlineField("Location", location);
                builder.Send();
            }

            public static IEnumerable<CodeInstruction> Transpiler(
                MethodBase original,
                IEnumerable<CodeInstruction> instructions)
            {
                var auctionHouseType = original.DeclaringType;
                var list = instructions.ToList();
                foreach (var isn in list)
                {
                    if (isn.opcode != OpCodes.Callvirt)
                        continue;
                    var called = (MethodBase)isn.operand;
                    if (called.Name != "RaiseEvent")
                        continue;
                    var parameters = called.GetParameters();
                    if (parameters.Length != 8)
                        continue;
                    if (parameters[0].ParameterType != auctionHouseType)
                        continue;
                    if (parameters[2].ParameterType != typeof(int) || parameters[3].ParameterType != typeof(int))
                        continue;
                    if (parameters[4].ParameterType != typeof(ulong))
                        continue;
                    if (parameters[5].ParameterType != typeof(string) || parameters[6].ParameterType != typeof(string))
                        continue;
                    if (parameters[7].ParameterType != typeof(EndpointId))
                        continue;
                    isn.opcode = OpCodes.Call;
                    isn.operand = RaiseEventShimRef.MakeGenericMethod(auctionHouseType);
                    Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy)).LogInformation(
                        "Patched sell offer for auction house from {Assembly}", original.DeclaringType?.Assembly.GetName().Name);
                }

                return list;
            }
        }
    }
}