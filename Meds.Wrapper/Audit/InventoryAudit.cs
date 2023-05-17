using HarmonyLib;
using Meds.Wrapper.Shim;
using Sandbox.Game;
using Sandbox.Game.Players;
using VRage.Game;
using VRage.Network;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    [HarmonyPatch(typeof(MyInventory), "TransferItemsInternal")]
    [AlwaysPatch]
    public static class InventoryAudit
    {
        public static void Prefix(MyInventory src, int index, out MyDefinitionId __state)
        {
            __state = src.Items[index]?.DefinitionId ?? default;
        }

        public static void Postfix(MyInventory src, MyInventory dst, int index, int destItemIndex, int amount, MyDefinitionId __state)
        {
            if (__state.TypeId.IsNull || amount == 0 || MyEventContext.Current.IsLocallyInvoked)
                return;
            var caller = MyEventContext.Current.Sender;
            var actor = MyPlayers.Static?.GetPlayer(caller);
            if (actor == null)
                return;

            var srcOwner = MyPlayers.Static?.GetControllingPlayer(src.Entity);
            var dstOwner = MyPlayers.Static?.GetControllingPlayer(dst.Entity);

            if (srcOwner == actor)
            {
                AuditPayload.Create(AuditEvent.ItemDeposit, actor, dstOwner, dst.Entity?.GetPosition())
                    .InventoryOpPayload(InventoryOpPayload.Create(src, dst, __state, amount))
                    .Emit();
            }
            else if (dstOwner == actor)
            {
                AuditPayload.Create(AuditEvent.ItemWithdraw, actor, srcOwner, src.Entity?.GetPosition())
                    .InventoryOpPayload(InventoryOpPayload.Create(src, dst, __state, amount))
                    .Emit();
            }
            else
            {
                AuditPayload.Create(AuditEvent.ItemTransfer, actor, srcOwner ?? dstOwner, (src.Entity ?? dst.Entity)?.GetPosition())
                    .InventoryOpPayload(InventoryOpPayload.Create(src, dst, __state, amount))
                    .Emit();
            }
        }
    }
}