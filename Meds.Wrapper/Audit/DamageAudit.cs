using Meds.Wrapper.Shim;
using Sandbox.Game.Entities.Entity.Stats;
using Sandbox.Game.Entities.Entity.Stats.Extensions;
using Sandbox.Game.EntityComponents.Character;
using Sandbox.Game.GameSystems;
using VRage.Components.Interfaces;
using VRage.Game.Entity;

namespace Meds.Wrapper.Audit
{
    public static class DamageAudit
    {
        public static void Register()
        {
            MyDamageSystem.Static?.RegisterAfterDamageHandler(10000, obj => OnDamage(AuditEvent.Damage, in obj));
            ((MyEntityStatComponent)null).RegisterOnDiedEvent(OnEntityDied);
        }

        private static void OnEntityDied(MyEntity entity)
        {
            var obj = entity.Get<MyCharacterDamageComponent>()?.LastDamage ?? default;
            obj.DamagedEntity = entity;
            OnDamage(AuditEvent.Death, in obj);
        }

        private static void OnDamage(AuditEvent evt, in MyDamageInformation obj)
        {
            var attacker = AuditPayload.PlayerForEntity(obj.Attacker);
            var damaged = AuditPayload.PlayerForEntity(obj.DamagedEntity);
            var extraInfo = obj.Attacker?.Components.Get<MedsDamageAttributionComponent>();
            if (attacker == null && damaged == null && extraInfo?.ShootingPlayer == null) return;
            var payload = AuditPayload.Create(
                    evt, attacker, damaged,
                    position: obj.HitInfo?.Position ?? obj.DamagedEntity?.GetPosition() ?? obj.Attacker?.GetPosition())
                .DamagePayload(DamagePayload.Create(in obj));
            payload.ActingPlayer ??= extraInfo?.ShootingPlayer;
            if (extraInfo?.ShootingEntity != null) payload.Damage.Attacker = extraInfo.ShootingEntity;
            payload.Emit();
        }
    }
}