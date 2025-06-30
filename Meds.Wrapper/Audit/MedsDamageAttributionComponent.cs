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

        public MyEntity ShootingEntityRuntime
        {
            set
            {
                var player = MyPlayers.Static.GetControllingPlayer(value);
                var existing = value.Components.Get<MedsDamageAttributionComponent>();
                if (player != null)
                {
                    ShootingPlayer = PlayerPayload.Create(player);
                    ShootingEntity = null;
                }
                else
                {
                    ShootingPlayer = existing?.ShootingPlayer;
                    ShootingEntity = BasicEntityPayload.Create(value);
                }
            }
        }

        public MedsDamageAttributionComponent()
        {
        }

        public static void Apply(MyEntityComponentContainer container, MyEntity shooter) =>
            container.GetOrAdd<MedsDamageAttributionComponent>().ShootingEntityRuntime = shooter;
    }
}