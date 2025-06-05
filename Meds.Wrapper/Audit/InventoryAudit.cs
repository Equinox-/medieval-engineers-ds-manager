using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.Entities.Components.Crafting;
using Medieval.Entities.Inventory;
using Meds.Wrapper.Shim;
using Sandbox.Game;
using Sandbox.Game.Entities.Inventory;
using VRage.Game;
using VRage.Game.Entity;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    public static class InventoryAudit
    {
        public static void EmitEvent(MyInventoryBase src, MyInventoryBase dst, MyDefinitionId id, int amount)
        {
            if (id.TypeId.IsNull || amount == 0) return;
            var actor = AuditPayload.GetActingPlayer();
            if (actor == null) return;

            var srcOwner = AuditPayload.PlayerForEntity(src.Entity);
            var dstOwner = AuditPayload.PlayerForEntity(dst.Entity);

            if (srcOwner == actor)
            {
                AuditPayload.Create(AuditEvent.ItemDeposit, actor, dstOwner, dst.Entity?.GetPosition())
                    .InventoryOpPayload(InventoryOpPayload.Create(src, dst, id, amount))
                    .Emit();
            }
            else if (dstOwner == actor)
            {
                AuditPayload.Create(AuditEvent.ItemWithdraw, actor, srcOwner, src.Entity?.GetPosition())
                    .InventoryOpPayload(InventoryOpPayload.Create(src, dst, id, amount))
                    .Emit();
            }
            else
            {
                AuditPayload.Create(AuditEvent.ItemTransfer, actor, srcOwner ?? dstOwner, (src.Entity ?? dst.Entity)?.GetPosition())
                    .InventoryOpPayload(InventoryOpPayload.Create(src, dst, id, amount))
                    .Emit();
            }
        }

        [HarmonyPatch(typeof(MyInventory), "TransferItemsInternal")]
        [AlwaysPatch]
        public static class TransferItemsInternal
        {
            public static void Prefix(MyInventory src, int index, out MyDefinitionId __state)
            {
                __state = src.Items[index]?.DefinitionId ?? default;
            }

            public static void Postfix(MyInventory src, MyInventory dst, int amount, MyDefinitionId __state) => EmitEvent(src, dst, __state, amount);
        }

        [HarmonyPatch]
        [AlwaysPatch]
        public static class TransferItemsFrom
        {
            public static IEnumerable<MethodBase> TargetMethods() => new[] { typeof(MyInventory), typeof(MyInventoryAggregate), typeof(MyAreaInventory), typeof(MyGroundInventory) }
                .Select(type => AccessTools.Method(type, nameof(MyInventoryBase.TransferItemsFrom), new[]
                {
                    typeof(MyInventoryBase),
                    typeof(MyInventoryItem),
                    typeof(int)
                }))
                .Where(x => x != null);

            public static void Prefix(MyInventoryItem item, out MyDefinitionId __state) => __state = item?.DefinitionId ?? default;

            public static void Postfix(MyInventoryBase __instance, MyInventoryBase sourceInventory, MyInventoryItem item, int amount, bool __result,
                MyDefinitionId __state)
            {
                if (!__result) return;
                EmitEvent(sourceInventory, __instance, __state, amount);
            }
        }

        [HarmonyPatch(typeof(MyCraftingComponent), "MoveItemsToInput")]
        [AlwaysPatch]
        public static class CraftingMoveItemsToInput
        {
            private static bool RemoveInventoryShim(MyInventoryBase from, MyDefinitionId id, int amount, MyInventoryBase inputInventory)
            {
                var result = from.RemoveItems(id, amount);
                if (result) EmitEvent(from, inputInventory, id, amount);
                return result;
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var inputInventory = AccessTools.Field(typeof(MyCraftingComponent), "m_inputInventory");
                var removeItems = AccessTools.Method(typeof(MyInventoryBase), nameof(MyInventoryBase.RemoveItems),
                    new[] { typeof(MyDefinitionId), typeof(int) });
                var shim = AccessTools.Method(typeof(CraftingMoveItemsToInput), nameof(RemoveInventoryShim));
                foreach (var ins in instructions)
                {
                    if (shim != null && inputInventory != null && removeItems != null && ins.Calls(removeItems))
                    {
                        yield return ins.ChangeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Ldfld, inputInventory);
                        yield return new CodeInstruction(OpCodes.Call, shim);
                    }
                    else
                        yield return ins;
                }
            }
        }
    }
}