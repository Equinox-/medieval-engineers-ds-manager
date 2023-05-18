using HarmonyLib;
using Meds.Wrapper.Shim;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using Sandbox.Game.World;
using VRage.Network;

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
    }
}