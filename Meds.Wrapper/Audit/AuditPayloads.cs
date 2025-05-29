using System;
using Medieval.Entities.Components.Planet;
using Medieval.GameSystems;
using Medieval.GameSystems.Factions;
using Meds.Wrapper.Trace;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Players;
using VRage.Components;
using VRage.Components.Entity.CubeGrid;
using VRage.Components.Interfaces;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
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

        ClipboardCut,
        ClipboardPaste,

        FlyingStart,
        FlyingEnd,

        SpawnItems,

        ChatCommand,

        Damage,
    }

    public class AuditPayload
    {
        public string AuditEvent;

        public TraceReferencePayload? Trace;
        public PlayerPayload? ActingPlayer;
        public PlayerPayload? OwningPlayer;
        public PositionPayload? Position;
        public InventoryOpPayload? InventoryOp;
        public ControlOpPayload? ControlOp;
        public ClipboardOpPayload? ClipboardOp;
        public ChatCommandPayload? ChatCommand;
        public DamagePayload? Damage;

        public static MyPlayer PlayerForEntity(MyEntity entity)
        {
            if (entity == null) return null;
            var player = MyPlayers.Static?.GetControllingPlayer(entity);
            if (player != null) return player;
            return null;
        }

        public static AuditPayload Create(AuditEvent evt, MyPlayer acting = null, MyPlayer owning = null, Vector3D? owningLocation = null,
            Vector3D? position = null)
        {
            var payload = new AuditPayload
            {
                AuditEvent = MyEnum<AuditEvent>.GetName(evt),
                ActingPlayer = acting != null ? (PlayerPayload?)PlayerPayload.Create(acting) : null,
                Position = position != null && PositionPayload.TryCreate(position.Value, out var posPayload) ? posPayload
                    : PositionPayload.TryCreate(acting, out posPayload) ? (PositionPayload?)posPayload : null
            };

            if (acting != null && MedievalMasterAudit.TryGetTrace(acting.Id.SteamId, out var trace))
                payload.Trace = trace.RefPayload();

            if (owning != null)
            {
                payload.OwningPlayer = PlayerPayload.Create(owning);
                return payload;
            }

            var pos = owningLocation ?? position ?? acting?.ControlledEntity?.GetPosition();
            if (payload.Position == null || pos == null) return payload;
            var planet = MyGamePruningStructureSandbox.GetClosestPlanet(pos.Value);
            if (planet == null) return payload;
            var areas = planet.Get<MyPlanetAreasComponent>();
            var areaOwnership = planet.Get<MyPlanetAreaOwnershipComponent>();
            if (areas == null || areaOwnership == null) return payload;
            var owner = areaOwnership.GetAreaOwner(areas.GetArea((Vector3)Vector3D.Transform(pos.Value, planet.WorldMatrix)));
            if (owner == 0 || owner == (acting?.Identity?.Id ?? 0)) return payload;
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

        public AuditPayload ClipboardOpPayload(in ClipboardOpPayload payload)
        {
            ClipboardOp = payload;
            return this;
        }

        public AuditPayload ChatCommandPayload(in ChatCommandPayload payload)
        {
            ChatCommand = payload;
            return this;
        }

        public AuditPayload DamagePayload(in DamagePayload payload)
        {
            Damage = payload;
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

            log.Logger.ZLogInformationWithPayload(this, "{0} by {1} on {2} ({3})", AuditEvent, ActingPlayer?.DisplayName,
                InventoryOp?.To.Entity?.Subtype ?? InventoryOp?.From.Entity?.Subtype ?? ControlOp?.Basic.Entity?.Subtype ?? Damage?.Damaged?.Subtype,
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

        public BasicEntityComponentPayload From, To;

        public static InventoryOpPayload Create(MyInventory src, MyInventory dst, MyDefinitionId id, int amount) => new InventoryOpPayload
        {
            Subtype = id.SubtypeName,
            Amount = amount,
            From = BasicEntityComponentPayload.Create(src),
            To = BasicEntityComponentPayload.Create(dst),
        };
    }

    public struct ControlOpPayload
    {
        public BasicEntityComponentPayload Basic;
        public string Slot;

        public static ControlOpPayload Create(MyEntityComponent component, MyEntityComponentDefinition componentDefinition = null, string slot = null) =>
            new ControlOpPayload
            {
                Basic = BasicEntityComponentPayload.Create(component, componentDefinition),
                Slot = slot
            };
    }

    public struct ClipboardOpPayload
    {
        public int Grids;
        public int Blocks;

        public void Add(MyGridDataComponent grid)
        {
            Grids++;
            Blocks += grid.BlockCount;
        }

        public void Add(MyEntity entity)
        {
            if (entity.Components.TryGet(out MyGridDataComponent grid))
                Add(grid);
        }
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

    public struct ChatCommandPayload
    {
        public string Prefix;
        public string Command;
    }

    public struct DamagePayload
    {
        public string Type;
        public float Amount;
        public BasicEntityPayload? Damaged;
        public float? Velocity;
        public float? Distance;

        public static DamagePayload Create(in MyDamageInformation info) => new DamagePayload
        {
            Type = info.Type.String,
            Amount = info.Amount,
            Damaged = BasicEntityPayload.CreateNullable(info.DamagedEntity),
            Velocity = info.HitInfo != null ? MaybeLength(info.HitInfo.Value.Velocity) : null,
            Distance = info.HitInfo != null ? MaybeDistance(info.HitInfo.Value.StartPosition, info.HitInfo.Value.Position) : null,
        };

        private static float? MaybeLength(Vector3 vel)
        {
            var mag2 = vel.LengthSquared();
            return mag2 > 0 ? (float?)Math.Sqrt(mag2) : null;
        }

        private static float? MaybeDistance(Vector3D a, Vector3D b)
        {
            if (MathHelper.IsZero(a.LengthSquared()) || MathHelper.IsZero(b.LengthSquared())) return null;
            return MaybeLength((Vector3)(a - b));
        }
    }
}