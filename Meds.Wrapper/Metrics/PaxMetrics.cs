using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.GameSystems;
using Meds.Metrics;
using Meds.Shared;
using Meds.Wrapper.Shim;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;
using ZLogger;

namespace Meds.Wrapper.Metrics
{
    public class PaxMetrics
    {
        public static void Register(MetricConfig config)
        {
            PatchHelper.Patch(typeof(PatchAuctionHouseBuy));
            PatchHelper.Patch(typeof(PatchAuctionHouseSell));

            var waterSurface = PatchHelper.ModTypes("Pax.Water.PAX_WaterSurface").FirstOrDefault().type;
            if (waterSurface != null)
                WaterMetrics(waterSurface);
        }


        private static void WaterMetrics(Type waterSurface)
        {
            var group = MetricRegistry.Group(MetricName.Of("me.pax.water"));
            void MaybeRegister(string name, FieldInfo field)
            {
                if (field == null || !typeof(ICollection).IsAssignableFrom(field.FieldType))
                    return;
                group.Gauge(name, () =>
                {
                    var sc = MySession.Static?.Components;
                    if (sc == null || !sc.TryGet(waterSurface, out var comp))
                        return double.NaN;
                    var collection = field.GetValue(comp) as ICollection;
                    return collection?.Count ?? double.NaN;
                });
            }
            MaybeRegister("floating.grids", AccessTools.Field(waterSurface, "m_grids"));
            MaybeRegister("floating.entities", AccessTools.Field(waterSurface, "m_entities"));
            MaybeRegister("sinking", AccessTools.Field(waterSurface, "m_sinkingData"));
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

            private static List<MethodBase> _methods;

            private static IEnumerable<MethodBase> TargetMethodsInternal()
            {
                foreach (var (mod, type) in PatchHelper.ModTypes(AuctionHouseType))
                {
                    var method = AccessTools.Method(type, "PlaceBuyOrder");
                    if (method == null)
                    {
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy)).ZLogInformation(
                            "When patching AuctionHouse type from {0} ({1}) the PlaceBuyOrder method wasn't found",
                            mod.Id, mod.Name);
                        continue;
                    }

                    yield return method;
                }
            }

            public static bool Prepare()
            {
                _methods = TargetMethodsInternal().ToList();
                return _methods.Count > 0;
            }

            public static IEnumerable<MethodBase> TargetMethods() => _methods;

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
                    "crafter", PatchHelper.SubtypeOrDefault(owner.DefinitionId),
                    "item", PatchHelper.SubtypeOrDefault(boughtItem),
                    "gold", PatchHelper.SubtypeOrDefault(goldDefinition));
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
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy)).ZLogInformation(
                            "Patched buy order for auction house from {0}, {1}",
                            original.DeclaringType?.Assembly.GetName().Name, foundGold);
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
            private static List<MethodBase> _methods;

            private static IEnumerable<MethodBase> TargetMethodsInternal()
            {
                foreach (var (mod, type) in PatchHelper.ModTypes(AuctionHouseType))
                {
                    var method = AccessTools.Method(type, "PlaceSellOrder");
                    if (method == null)
                    {
                        Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy))
                            .ZLogInformation(
                                "When patching AuctionHouse type from {0} ({1}) the PlaceSellOrder method wasn't found",
                                mod.Id, mod.Name);
                        continue;
                    }

                    yield return method;
                }
            }

            public static bool Prepare()
            {
                _methods = TargetMethodsInternal().ToList();
                return _methods.Count > 0;
            }

            public static IEnumerable<MethodBase> TargetMethods() => _methods;

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
                    Entrypoint.LoggerFor(typeof(PatchAuctionHouseBuy)).ZLogInformation(
                        "Patched sell offer for auction house from {0}",
                        original.DeclaringType?.Assembly.GetName().Name);
                }

                return list;
            }
        }
    }
}