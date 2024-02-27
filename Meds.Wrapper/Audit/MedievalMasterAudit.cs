using System.Collections.Generic;
using HarmonyLib;
using Medieval.GameSystems.Building;
using Meds.Wrapper.Shim;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using VRage.Game.Components;
using VRage.Network;
using VRage.Scene;
using VRageMath;

namespace Meds.Wrapper.Audit
{
    public static class MedievalMasterAudit
    {
        [HarmonyPatch(typeof(MySession), "OnAdminModeEnabled")]
        [AlwaysPatch]
        public static class AuditMmStartStop
        {
            public static void Postfix()
            {
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var wasEnabled = MySession.Static?.IsAdminModeEnabled(userId) ?? false;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;
                AuditPayload.Create(
                        wasEnabled ? AuditEvent.MedievalMasterStart : AuditEvent.MedievalMasterStop,
                        player)
                    .Emit();
            }
        }

        [HarmonyPatch(typeof(MyGridPlacer), "ProcessPasting")]
        [AlwaysPatch]
        public static class AuditPasteGrids
        {
            public static void Postfix(List<MyGridPlacer.MergeScene> createdScenes)
            {
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;
                var clipboard = new ClipboardOpPayload();
                var bounds = BoundingBoxD.CreateInvalid();
                foreach (var scene in createdScenes)
                foreach (var grid in scene.Grids)
                {
                    clipboard.Add(grid);
                    bounds.Include(grid.Container.Get<MyPositionComponentBase>().WorldAABB);
                }

                if (clipboard.Grids == 0)
                    return;

                AuditPayload.Create(AuditEvent.ClipboardPaste, player, owningLocation: bounds.Center)
                    .ClipboardOpPayload(in clipboard)
                    .Emit();
            }
        }

        [HarmonyPatch(typeof(MyClipboardComponent), "OnGridClosedRequest")]
        [AlwaysPatch]
        public static class AuditCutGrids
        {
            public static void Prefix(EntityId entityId)
            {
                var scene = MySession.Static.Scene;
                if (!scene.TryGetEntity(entityId, out var entity))
                    return;
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;

                var clipboard = new ClipboardOpPayload();
                var bounds = BoundingBoxD.CreateInvalid();

                var collector = scene.GetCollector();
                collector.CollectAllConnectedEntities(entity);

                foreach (var attached in collector.Entities)
                {
                    clipboard.Add(attached);
                    bounds.Include(attached.PositionComp.WorldAABB);
                }

                if (clipboard.Grids == 0)
                    return;

                AuditPayload.Create(AuditEvent.ClipboardCut, player, owningLocation: bounds.Center)
                    .ClipboardOpPayload(in clipboard)
                    .Emit();
            }
        }
    }
}