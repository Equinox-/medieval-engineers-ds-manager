using Sandbox.Game.GameSystems;
using VRage.Components.Interfaces;

namespace Meds.Wrapper.Audit
{
    public static class DamageAudit
    {
        public static void Register() => MyDamageSystem.Static?.RegisterAfterDamageHandler(10000, OnDamage);

        private static void OnDamage(MyDamageInformation obj)
        {
            var attacker = AuditPayload.PlayerForEntity(obj.Attacker);
            var damaged = AuditPayload.PlayerForEntity(obj.DamagedEntity);
            if (attacker == null && damaged == null) return;
            AuditPayload.Create(
                    AuditEvent.Damage, attacker, damaged,
                    position: obj.HitInfo?.Position ?? obj.DamagedEntity?.GetPosition() ?? obj.Attacker?.GetPosition())
                .DamagePayload(DamagePayload.Create(in obj))
                .Emit();
        }
    }
}