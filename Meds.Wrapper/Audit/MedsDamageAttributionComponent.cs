using Meds.Wrapper.Utils;
using Sandbox.Game.Players;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace Meds.Wrapper.Audit
{
    [MyComponent]
    public class MedsDamageAttributionComponent : MyEntityComponent
    {
        public PlayerPayload? ShootingPlayer;
        public BasicEntityPayload? ShootingEntity;

        public MedsDamageAttributionComponent()
        {
        }

        public MedsDamageAttributionComponent(MyEntity shooter)
        {
            var player = MyPlayers.Static.GetControllingPlayer(shooter);
            var existing = shooter.Components.Get<MedsDamageAttributionComponent>();
            if (player != null)
            {
                ShootingPlayer = PlayerPayload.Create(player);
                ShootingEntity = null;
            }
            else
            {
                ShootingPlayer = existing?.ShootingPlayer;
                ShootingEntity = BasicEntityPayload.Create(shooter);
            }
        }
    }
}