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
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using VRage.Components.Entity.CubeGrid;
using VRage.Components.Interfaces;
using VRage.Entity.Block;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Utils;
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

        BlocksPlace,
        BlocksRemove,

        FlyingStart,
        FlyingEnd,

        SpawnItems,

        ChatCommand,

        Damage,
        Death,

        FactionCreate,
        FactionDelete,

        FactionApplicationCreate,
        FactionApplicationAccept,
        FactionApplicationReject,

        FactionDiplomacyRequest,
        FactionDiplomacyAccept,

        FactionMemberSetRank,
        FactionMemberLeft,
        FactionMemberKicked,
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
        public DamagePayload Damage;
        public FactionPayload? Faction;
        public PlayerPayload? FactionMember;
        public DiplomacyPayload Diplomacy;

        public static MyPlayer GetActingPlayer(long? identity = null, ulong? steam = null)
        {
            if (steam != null)
            {
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(steam.Value));
                if (player != null) return player;
            }

            // ReSharper disable once InvertIf
            if (identity != null)
            {
                var id = MyIdentities.Static?.GetIdentity(identity.Value);
                var player = id != null ? MyPlayers.Static?.GetPlayer(id) : null;
                if (player != null) return player;
            }

            return MyPlayers.Static?.GetPlayer(MyEventContext.Current.IsLocallyInvoked ? new EndpointId(Sync.MyId) : MyEventContext.Current.Sender);
        }

        public static MyPlayer PlayerForEntity(MyEntity entity)
        {
            if (entity == null) return null;
            var player = MyPlayers.Static?.GetControllingPlayer(entity);
            if (player != null) return player;
            return null;
        }

        public static AuditPayload CreateWithoutPosition(AuditEvent evt, MyPlayer acting = null)
        {
            acting ??= GetActingPlayer();
            var payload = new AuditPayload
            {
                AuditEvent = MyEnum<AuditEvent>.GetName(evt),
                ActingPlayer = acting != null ? (PlayerPayload?)PlayerPayload.Create(acting) : null,
            };

            if (acting != null && MedievalMasterAudit.TryGetTrace(acting.Id.SteamId, out var trace))
                payload.Trace = trace.RefPayload();

            return payload;
        }

        public static AuditPayload Create(AuditEvent evt, MyPlayer acting = null, MyPlayer owning = null, Vector3D? owningLocation = null,
            Vector3D? position = null)
        {
            var payload = CreateWithoutPosition(evt, acting);
            payload.Position = position != null && PositionPayload.TryCreate(position.Value, out var posPayload) ? posPayload
                : PositionPayload.TryCreate(acting, out posPayload) ? (PositionPayload?)posPayload : null;

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

        public AuditPayload DamagePayload(DamagePayload payload)
        {
            Damage = payload;
            return this;
        }

        public AuditPayload FactionPayload(in FactionPayload payload)
        {
            Faction = payload;
            return this;
        }

        public AuditPayload FactionMemberPayload(in PlayerPayload payload)
        {
            FactionMember = payload;
            return this;
        }

        public AuditPayload DiplomacyPayload(DiplomacyPayload payload)
        {
            Diplomacy = payload;
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
        public string BlockSubtype;
        public int Grids;
        public int Blocks;

        public void Add(MyBlock block)
        {
            if (Blocks == 0)
                BlockSubtype = block.DefinitionId.SubtypeName;
            else if (BlockSubtype != null && BlockSubtype != block.DefinitionId.SubtypeName)
                BlockSubtype = null;
            Blocks++;
        }

        public void Add(MyGridDataComponent grid)
        {
            if ((Blocks == 0 || BlockSubtype != null) && grid.BlockCount > 0)
            {
                var type = HomogenousBlockType(grid);
                if (Blocks == 0)
                    BlockSubtype = type; // Set homogenous block type if there aren't already blocks in the clipboard.
                else if (type != BlockSubtype)
                    BlockSubtype = null; // Clear homogenous block type if the added grid isn't homogenous or is homogenous but differs.
            }
            Grids++;
            Blocks += grid.BlockCount;
        }

        private static string HomogenousBlockType(MyGridDataComponent grid)
        {
            var homogenousSubtype = MyStringHash.NullOrEmpty;
            foreach (var block in grid.Blocks)
            {
                var blockSubtype = block.DefinitionId.SubtypeId;
                if (homogenousSubtype == blockSubtype)
                    continue;
                if (homogenousSubtype != MyStringHash.NullOrEmpty)
                    return null;
                homogenousSubtype = blockSubtype;
            }

            return homogenousSubtype.String;
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
        public string FactionRank;

        public static PlayerPayload Create(MyPlayer player) => CreateInternal(player, player.Identity);

        public static PlayerPayload Create(MyIdentity identity) => CreateInternal(MyPlayers.Static?.GetPlayer(identity), identity);

        private static PlayerPayload CreateInternal(MyPlayer player, MyIdentity identity)
        {
            var faction = identity != null ? MyFactionManager.GetPlayerFaction(identity.Id) : null;
            return new PlayerPayload
            {
                SteamId = player?.Id.SteamId ?? 0,
                DisplayName = identity?.DisplayName,
                Faction = faction != null ? (FactionPayload?)FactionPayload.Create(faction) : null,
                FactionRank = faction?.GetMemberRank(identity.Id)?.Title,
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

    public class DamagePayload
    {
        public string Type;
        public float Amount;
        public BasicEntityPayload? Damaged;
        public float? Velocity;
        public float? Distance;

        public static DamagePayload Create(in MyDamageInformation info) => new DamagePayload
        {
            Type = info.Type == MyStringHash.NullOrEmpty ? "Unknown" : info.Type.String,
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

    public class DiplomacyPayload
    {
        public string Status;
        public PlayerPayload? OtherPlayer;
        public FactionPayload? OtherFaction;

        public static DiplomacyPayload Create(MyStringHash status, in PlayerPayload player) => new DiplomacyPayload
        {
            Status = status.String,
            OtherPlayer = player,
        };

        public static DiplomacyPayload Create(MyStringHash status, in FactionPayload faction) => new DiplomacyPayload
        {
            Status = status.String,
            OtherFaction = faction,
        };
    }
}