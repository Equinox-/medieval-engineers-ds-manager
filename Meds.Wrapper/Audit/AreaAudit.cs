using System;
using Medieval.Entities.Components.Planet;
using Medieval.GameSystems;
using Medieval.ObjectBuilders.Components;
using Sandbox.Game.Players;
using Sandbox.Game.World;
using VRage.Library.Utils;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    public static class AreaAudit
    {
        public static void Register() => MyPlanetAreaOwnershipComponent.OnAreaStateChanged += OnAreaStateChanged;

        private static void OnAreaStateChanged(
            MyPlanetAreaOwnershipComponent component,
            long areaId,
            MyObjectBuilder_PlanetAreaOwnershipComponent.AreaState oldState,
            MyObjectBuilder_PlanetAreaOwnershipComponent.AreaState newState,
            long oldOwnerId,
            long currentOwnerId)
        {
            var areas = component.Container?.Get<MyPlanetAreasComponent>();
            if (areas == null) return;
            var upkeep = component.Container?.Get<MyPlanetAreaUpkeepComponent>();
            var pos = areas.CalculateAreaCenter(areaId);

            var oldOwner = oldOwnerId != 0 ? MyIdentities.Static?.GetIdentity(oldOwnerId) : null;
            var currentOwner = currentOwnerId != 0 ? MyIdentities.Static?.GetIdentity(currentOwnerId) : null;

            AuditPayload
                .Create(AuditEvent.AreaStateChange, position: pos, owningLocation: pos)
                .AreaPayload(new AreaPayload
                {
                    Id = areaId,
                    State = MyEnum<MyObjectBuilder_PlanetAreaOwnershipComponent.AreaState>.GetName(newState),
                    Owner = currentOwner != null ? (PlayerPayload?)PlayerPayload.Create(currentOwner) : null,

                    PriorState = oldState != newState ? MyEnum<MyObjectBuilder_PlanetAreaOwnershipComponent.AreaState>.GetName(oldState) : null,
                    PriorOwner = oldOwner != null ? (PlayerPayload?)PlayerPayload.Create(oldOwner) : null,

                    ExpiresInHours = currentOwner != null && upkeep != null && !upkeep.IsTaxFree(currentOwner.Id)
                        ? (double?)Math.Max(0, (TimeSpan.FromMilliseconds(upkeep.GetExpirationTime(areaId)) - MySession.Static.ElapsedGameTime).TotalHours)
                        : null,
                })
                .Emit();
        }
    }
}