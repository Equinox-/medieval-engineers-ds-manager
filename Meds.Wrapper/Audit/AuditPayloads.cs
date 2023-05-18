using System;
using Medieval.Entities.Components.Planet;
using Medieval.GameSystems;
using Medieval.GameSystems.Factions;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Players;
using VRage.Game;
using VRage.Library.Utils;
using VRageMath;
using ZLogger;

namespace Meds.Wrapper.Audit
{
    public enum AuditEvent
    {
        ItemDeposit,
        ItemWithdraw,
        ItemTransfer,

        EquiControlStart,

        PaxRopeControlStart,

        MedievalMasterStart,
        MedievalMasterStop,
    }

    public class AuditPayload
    {
        public string AuditEvent;
        public PlayerPayload ActingPlayer;
        public PlayerPayload? OwningPlayer;
        public PositionPayload? Position;

        public InventoryOpPayload? InventoryOp;

        public ControlOpPayload? ControlOp;

        public static AuditPayload Create(AuditEvent evt, MyPlayer acting, MyPlayer owning = null, Vector3D? owningLocation = null)
        {
            var payload = new AuditPayload
            {
                AuditEvent = MyEnum<AuditEvent>.GetName(evt),
                ActingPlayer = PlayerPayload.Create(acting),
                Position = PositionPayload.TryCreate(acting, out var posPayload) ? (PositionPayload?)posPayload : null
            };
            if (owning != null)
            {
                payload.OwningPlayer = PlayerPayload.Create(owning);
                return payload;
            }

            var pos = owningLocation ?? acting.ControlledEntity?.GetPosition();
            if (payload.Position == null || pos == null) return payload;
            var planet = MyGamePruningStructureSandbox.GetClosestPlanet(pos.Value);
            if (planet == null) return payload;
            var areas = planet.Get<MyPlanetAreasComponent>();
            var areaOwnership = planet.Get<MyPlanetAreaOwnershipComponent>();
            if (areas == null || areaOwnership == null) return payload;
            var owner = areaOwnership.GetAreaOwner(areas.GetArea((Vector3)Vector3D.Transform(pos.Value, planet.WorldMatrix)));
            if (owner == 0 || owner == (acting.Identity?.Id ?? 0)) return payload;
            var id = MyIdentities.Static.GetIdentity(owner);
            if (id == null) return payload;
            payload.OwningPlayer = PlayerPayload.Create(id);

            return payload;
        }

        public AuditPayload InventoryOpPayload(in InventoryOpPayload payload)
        {
            InventoryOp = payload;
            return this;
        }

        public AuditPayload ControlOpPayload(in ControlOpPayload payload)
        {
            ControlOp = payload;
            return this;
        }

        private static AuditLoggerHolder _logger;

        public void Emit()
        {
            if (AuditEvent == null)
                throw new ArgumentException();
            var log = _logger;
            var instance = Entrypoint.Instance;
            if (log == null || log.Owner != instance)
            {
                lock (typeof(AuditPayload))
                {
                    _logger = log = new AuditLoggerHolder
                    {
                        Owner = instance,
                        Logger = instance.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Audit"),
                    };
                }
            }

            log.Logger.ZLogInformationWithPayload(this, "{0} by {1} on {2} ({3})", AuditEvent, ActingPlayer.DisplayName,
                    InventoryOp?.ToEntity ?? InventoryOp?.FromEntity ?? ControlOp?.Entity,
                    OwningPlayer?.DisplayName);
        }

        private sealed class AuditLoggerHolder
        {
            public IHost Owner;
            public ILogger Logger;
        }
    }

    public struct InventoryOpPayload
    {
        public string Subtype;
        public int Amount;

        public string FromEntity;
        public string FromInventory;
        public string ToEntity;
        public string ToInventory;

        public static InventoryOpPayload Create(MyInventory src, MyInventory dst, MyDefinitionId id, int amount) => new InventoryOpPayload
        {
            Subtype = id.SubtypeName,
            Amount = amount,
            FromEntity = src?.Entity?.DefinitionId?.SubtypeName,
            FromInventory = src?.SubtypeId.String,
            ToEntity = dst?.Entity?.DefinitionId?.SubtypeName,
            ToInventory = dst?.SubtypeId.String,
        };
    }

    public struct ControlOpPayload
    {
        public string Entity;
        public string Component;
        public string Slot;
    }

    public struct PlayerPayload
    {
        public ulong SteamId;
        public string DisplayName;

        public FactionPayload? Faction;

        public static PlayerPayload Create(MyPlayer player)
        {
            var faction = player.Identity != null ? MyFactionManager.GetPlayerFaction(player.Identity.Id) : null;

            return new PlayerPayload
            {
                SteamId = player.Id.SteamId,
                DisplayName = player.Identity?.DisplayName,
                Faction = faction != null ? (FactionPayload?)FactionPayload.Create(faction) : null
            };
        }

        public static PlayerPayload Create(MyIdentity identity)
        {
            var player = MyPlayers.Static?.GetPlayer(identity);
            var faction = MyFactionManager.GetPlayerFaction(identity.Id);

            return new PlayerPayload
            {
                SteamId = player?.Id.SteamId ?? 0,
                DisplayName = identity.DisplayName,
                Faction = faction != null ? (FactionPayload?)FactionPayload.Create(faction) : null
            };
        }
    }

    public struct FactionPayload
    {
        public long FactionId;
        public string FactionTag;
        public string FactionName;

        public static FactionPayload Create(MyFaction faction) => new FactionPayload
        {
            FactionId = faction.FactionId,
            FactionTag = faction.FactionTag,
            FactionName = faction.FactionName
        };
    }
}