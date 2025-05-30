using HarmonyLib;
using Medieval.GameSystems.Factions;
using Meds.Wrapper.Shim;
using Sandbox.Game.Players;
using VRage.Utils;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    public static class FactionAudit
    {
        public static void Register()
        {
            MyFactionManager.OnFactionCreated += OnFactionCreated;
            MyFaction.OnAppliedToFaction += OnAppliedToFaction;
            MyFaction.OnApplicationProcessed += OnApplicationProcessed;
            MyFaction.OnSetDiplomaticStatus += OnSetDiplomaticStatus;
            MyFaction.OnSetMemberRank += OnSetMemberRank;
            MyFaction.OnLeaveFaction += OnLeaveFaction;
            MyFaction.OnKickMember += OnKickMember;
        }

        private static void OnFactionCreated(MyFactionManager _, long creatingPlayerId, MyFactionManager.FactionCreationResult result, MyFaction createdFaction)
        {
            var player = AuditPayload.GetActingPlayer(identity: creatingPlayerId);
            AuditPayload.CreateWithoutPosition(AuditEvent.FactionCreate, player)
                .FactionPayload(FactionPayload.Create(createdFaction))
                .Emit();
        }

        private static void OnAppliedToFaction(MyFaction appliedFaction, MyFaction.ApplyToFactionResult result, long applicantId)
        {
            var player = AuditPayload.GetActingPlayer(identity: applicantId);
            AuditPayload.CreateWithoutPosition(AuditEvent.FactionApplicationCreate, player)
                .FactionPayload(FactionPayload.Create(appliedFaction))
                .Emit();
        }

        private static void OnApplicationProcessed(MyFaction appliedFaction, MyFaction.ProcessApplicationResult result, long applicantId)
        {
            var payload = result switch
            {
                MyFaction.ProcessApplicationResult.Accepted => AuditPayload.CreateWithoutPosition(AuditEvent.FactionApplicationAccept),
                MyFaction.ProcessApplicationResult.Rejected => AuditPayload.CreateWithoutPosition(AuditEvent.FactionApplicationReject),
                _ => null
            };
            var applicant = MyIdentities.Static?.GetIdentity(applicantId);
            if (applicant != null) payload?.FactionMemberPayload(PlayerPayload.Create(applicant));
            payload?
                .FactionPayload(FactionPayload.Create(appliedFaction))
                .Emit();
        }

        private static void OnSetDiplomaticStatus(MyFaction faction, MyFaction.SetDiplomaticStatusResult result, DiplomaticPartyType partyType, long partyId,
            MyStringHash status)
        {
            var payload = result switch
            {
                MyFaction.SetDiplomaticStatusResult.Ok => AuditPayload.CreateWithoutPosition(AuditEvent.FactionDiplomacyAccept),
                MyFaction.SetDiplomaticStatusResult.Pending => AuditPayload.CreateWithoutPosition(AuditEvent.FactionDiplomacyRequest),
                _ => null
            };
            if (payload == null) return;
            payload.FactionPayload(FactionPayload.Create(faction));
            switch (partyType)
            {
                case DiplomaticPartyType.Player:
                    var playerParty = MyIdentities.Static?.GetIdentity(partyId);
                    if (playerParty != null) payload.DiplomacyPayload(DiplomacyPayload.Create(status, PlayerPayload.Create(playerParty)));
                    break;
                case DiplomaticPartyType.Faction:
                    var factionParty = MyFactionManager.Instance?.GetFactionById(partyId);
                    if (factionParty != null) payload.DiplomacyPayload(DiplomacyPayload.Create(status, FactionPayload.Create(factionParty)));
                    break;
                default:
                    return;
            }

            payload.Emit();
        }

        private static void OnSetMemberRank(MyFaction faction, MyFaction.SetMemberRankResult result, long memberId, int _)
        {
            if (result != MyFaction.SetMemberRankResult.Ok) return;
            var member = MyIdentities.Static?.GetIdentity(memberId);
            if (member == null) return;
            AuditPayload.CreateWithoutPosition(AuditEvent.FactionMemberSetRank)
                .FactionPayload(FactionPayload.Create(faction))
                .FactionMemberPayload(PlayerPayload.Create(member))
                .Emit();
        }

        private static void OnLeaveFaction(MyFaction faction, MyFaction.LeaveFactionResult result, long memberId)
        {
            if (result != MyFaction.LeaveFactionResult.Ok) return;
            var member = MyIdentities.Static?.GetIdentity(memberId);
            if (member == null) return;
            AuditPayload.CreateWithoutPosition(AuditEvent.FactionMemberLeft)
                .FactionPayload(FactionPayload.Create(faction))
                .FactionMemberPayload(PlayerPayload.Create(member))
                .Emit();
        }

        private static void OnKickMember(MyFaction faction, MyFaction.KickMemberResult result, long memberId)
        {
            if (result != MyFaction.KickMemberResult.Ok) return;
            var member = MyIdentities.Static?.GetIdentity(memberId);
            if (member == null) return;
            AuditPayload.CreateWithoutPosition(AuditEvent.FactionMemberKicked)
                .FactionPayload(FactionPayload.Create(faction))
                .FactionMemberPayload(PlayerPayload.Create(member))
                .Emit();
        }

        [HarmonyPatch(typeof(MyFactionManager), nameof(MyFactionManager.DeleteFaction))]
        [AlwaysPatch]
        public static class OnFactionDeletedPatch
        {
            public static void Prefix(MyFactionManager __instance, long factionId, out MyFaction __state) => __state = __instance?.GetFactionById(factionId);

            public static void Postfix(MyFaction __state, bool __result)
            {
                if (__state != null && __result)
                    AuditPayload.CreateWithoutPosition(AuditEvent.FactionDelete)
                        .FactionPayload(FactionPayload.Create(__state))
                        .Emit();
            }
        }
    }
}