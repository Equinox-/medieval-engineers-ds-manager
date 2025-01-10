using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using Medieval.Entities.Components.Planet;
using Medieval.GameSystems;
using Medieval.GameSystems.Building;
using Medieval.GameSystems.Factions;
using Medieval.GameSystems.Tools;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Wrapper.Shim;
using Meds.Wrapper.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Players;
using VRage.Components.Entity.CubeGrid;
using VRage.Components.Interfaces;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Library.Collections;
using VRage.Network;
using VRageMath;

namespace Meds.Wrapper.Metrics
{
    public static class PlayerMetrics
    {
        private const string Players = "me.players";
        private const string PlayersNetwork = "me.players.network";
        private const string PlayersNetworkPing = "me.players.network.ping";
        private const string PlayerTags = "me.players.tags";

        private static readonly ConcurrentDictionary<ulong, PlayerMetricHolder> Holders = new ConcurrentDictionary<ulong, PlayerMetricHolder>();

        public static void Register()
        {
            MyGridPlacer.OnBlockPlaced += OnBlockPlaced;
            MyGridPlacer.OnGridPlaced += OnGridPlaced;
            PatchHelper.Patch(typeof(BlockRemoval));
            PatchHelper.Patch(typeof(BuilderAction));
            PatchHelper.Patch(typeof(DiggerToolCut));
            PatchHelper.Patch(typeof(DiggerToolFill));
            PatchHelper.Patch(typeof(InventoryItemTransfer));
            PatchHelper.Patch(typeof(DamageSystemDealt));
            PatchHelper.Patch(typeof(DamageSystemDestroyed));
        }

        internal static bool TryGetHolder(ulong steamId, out PlayerMetricHolder holder) => Holders.TryGetValue(steamId, out holder);

        public static void Update()
        {
            var players = MyPlayers.Static.GetAllPlayers();
            using (PoolManager.Get(out List<ulong> toRemove))
            {
                foreach (var maybeRemove in Holders)
                    if (!players.ContainsKey(new MyPlayer.PlayerId(maybeRemove.Key)))
                    {
                        maybeRemove.Value.Remove();
                        toRemove.Add(maybeRemove.Key);
                    }

                foreach (var remove in toRemove)
                    Holders.Remove(remove);

                foreach (var existing in players.Values)
                {
                    if (!Holders.TryGetValue(existing.Id.SteamId, out var holder))
                        Holders[existing.Id.SteamId] = holder = new PlayerMetricHolder(existing.Id.SteamId);
                    holder.Update(existing);
                }
            }
        }

        private static MyPlayer GetControllingPlayer(MyEntity holder)
        {
            var entity = holder;
            while (entity != null)
            {
                var player = MyPlayers.Static.GetControllingPlayer(entity);
                if (player != null) return player;
                entity = entity.Parent;
            }

            return null;
        }

        private static void OnGridPlaced(MyEntity builder, List<MyEntity> placedEntities)
        {
            var player = GetControllingPlayer(builder);
            if (!Holders.TryGetValue(player.Id.SteamId, out var metrics)) return;
            var blockCount = 0;
            foreach (var entity in placedEntities)
                if (entity.Components.TryGet(out MyGridDataComponent gridData))
                    blockCount += gridData.BlockCount;
            metrics.BlocksPlaced.Inc(blockCount);
        }

        private static void OnBlockPlaced(MyEntity builder, MyDefinitionId blockDefinition)
        {
            var player = GetControllingPlayer(builder);
            if (!Holders.TryGetValue(player.Id.SteamId, out var metrics)) return;
            metrics.BlocksPlaced.Inc();
        }

        internal sealed class PlayerMetricHolder
        {
            private readonly MetricGroup _group;
            private readonly MetricGroup _networkGroup;

            private readonly Gauge _entityId;
            private readonly Gauge _areaFace;
            private readonly Gauge _areaX;
            private readonly Gauge _areaY;

            private readonly Gauge _latitude;
            private readonly Gauge _longitude;
            private readonly Gauge _elevation;

            private readonly Gauge _areaPermitted;
            internal readonly Counter BlocksPlaced;
            internal readonly Counter BlocksRemoved;
            internal readonly Counter BlocksConstructed;
            internal readonly Counter BlocksDeconstructed;

            internal readonly Counter VoxelsDug;
            internal readonly Counter VoxelsDeposited;

            internal readonly Counter ItemsAcquired;
            internal readonly Counter ItemsLost;

            internal readonly Counter DamageTaken;
            internal readonly Counter DamageDestroyed;
            internal readonly Counter DamageDealt;

            internal readonly Counter BytesSent;
            internal readonly Counter BytesReceived;
            internal readonly Gauge StateGroupCount;
            internal readonly Gauge StateGroupDelay;
            // internal readonly Timer Ping;


            internal PlayerMetricHolder(ulong steamId)
            {
                _group = MetricRegistry.Group(MetricName.Of(Players, "steamId", steamId.ToString()));

                _entityId = _group.Gauge("entityId", double.NaN);
                _areaFace = _group.Gauge("areaFace", double.NaN);
                _areaX = _group.Gauge("areaX", double.NaN);
                _areaY = _group.Gauge("areaY", double.NaN);

                _latitude = _group.Gauge("posLatitude", double.NaN);
                _longitude = _group.Gauge("posLongitude", double.NaN);
                _elevation = _group.Gauge("posElevation", double.NaN);

                _areaPermitted = _group.Gauge("areaPermitted", double.NaN);
                BlocksPlaced = _group.Counter("blocksPlaced");
                BlocksRemoved = _group.Counter("blocksRemoved");
                BlocksConstructed = _group.Counter("blocksConstructed");
                BlocksDeconstructed = _group.Counter("blocksDeconstructed");
                VoxelsDug = _group.Counter("voxelsDug");
                VoxelsDeposited = _group.Counter("voxelsDeposited");

                DamageTaken = _group.Counter("damageTaken");
                DamageDestroyed = _group.Counter("damageDestroyed");
                DamageDealt = _group.Counter("damageDealt");

                ItemsAcquired = _group.Counter("itemsAcquired");
                ItemsLost = _group.Counter("itemsLost");

                
                _networkGroup = MetricRegistry.Group(_group.Name.WithSeries(PlayersNetwork));
                BytesSent = _networkGroup.Counter("bytes.sent");
                BytesReceived = _networkGroup.Counter("bytes.received");
                StateGroupCount = _networkGroup.Gauge("stateGroup.count", 0);
                StateGroupDelay = _networkGroup.Gauge("stateGroup.delay", 0);
                // Ping = MetricRegistry.Timer(_group.Name.WithSeries(PlayersNetworkPing));
            }

            internal void Update(MyPlayer player)
            {
                var name = player.Identity?.DisplayName;
                var faction = player.Identity != null ? MyFactionManager.GetPlayerFaction(player.Identity.Id) : null;
                MetricRegistry.Group(_group.Name
                        .WithTag("name", string.IsNullOrEmpty(name) ? "<empty>" : name)
                        .WithTag("faction", faction?.FactionTag ?? "<empty>")
                        .WithSeries(PlayerTags))
                    .Gauge("value", 1);

                ulong? entityId = null;
                PositionPayload positionPayload = default;
                var hasPositionPayload = false;

                var entity = player.ControlledEntity;
                if (entity != null)
                {
                    entityId = entity.Id.Value;
                    hasPositionPayload = PositionPayload.TryCreate(player, out positionPayload);
                }


                _entityId.SetValue(entityId ?? double.NaN);
                if (hasPositionPayload)
                {
                    _areaFace.SetValue(positionPayload.Face);
                    _areaX.SetValue(positionPayload.X);
                    _areaY.SetValue(positionPayload.Y);
                    _areaPermitted.SetValue(Gauge.ConvertValue(positionPayload.Permitted));

                    _longitude.SetValue(positionPayload.Lng);
                    _latitude.SetValue(positionPayload.Lat);
                    _elevation.SetValue(positionPayload.Elevation);
                }
                else
                {
                    _areaFace.SetValue(double.NaN);
                    _areaX.SetValue(double.NaN);
                    _areaY.SetValue(double.NaN);
                    _areaPermitted.SetValue(double.NaN);

                    _longitude.SetValue(double.NaN);
                    _latitude.SetValue(double.NaN);
                    _elevation.SetValue(double.NaN);
                }
            }

            internal void Remove()
            {
                MetricRegistry.RemoveMetric(_group.Name);
                MetricRegistry.RemoveMetric(_networkGroup.Name);
            }
        }

        private static void DiggerReport(MyDiggerToolBehavior behavior, bool filling)
        {
            var player = GetControllingPlayer(behavior.Holder);
            if (player == null) return;
            if (!Holders.TryGetValue(player.Id.SteamId, out var holder)) return;
            (filling ? holder.VoxelsDeposited : holder.VoxelsDug).Inc();
        }

        // ReSharper disable InconsistentNaming
        // ReSharper disable UnusedMember.Local
        [HarmonyPatch(typeof(MyGridPlacer), "Server_RemoveBlockInternal")]
        private static class BlockRemoval
        {
            public static void Postfix()
            {
                var steamId = MyEventContext.Current.Sender.Value;
                if (Holders.TryGetValue(steamId, out var holder))
                    holder.BlocksRemoved.Inc();
            }
        }

        [HarmonyPatch(typeof(MyBuilderToolBehavior), "UpdateBlockIntegrity")]
        private static class BuilderAction
        {
            public static void Postfix(MyPlayer buildingPlayer, GridBlockTuple block, int integrityChange)
            {
                if (integrityChange == 0) return;
                if (!Holders.TryGetValue(buildingPlayer.Id.SteamId, out var holder)) return;
                (integrityChange > 0 ? holder.BlocksConstructed : holder.BlocksDeconstructed).Inc();
                if (!block.GridData.Contains(block.Block.Id))
                    holder.BlocksDeconstructed.Inc();
            }
        }

        [HarmonyPatch(typeof(MyDiggerToolBehavior), "DoFillInServer")]
        private static class DiggerToolFill
        {
            public static void Postfix(MyDiggerToolBehavior __instance, ref bool __result)
            {
                if (!__result) return;
                DiggerReport(__instance, true);
            }
        }

        [HarmonyPatch(typeof(MyDiggerToolBehavior), "DoCutOutServer")]
        private static class DiggerToolCut
        {
            public static void Postfix(MyDiggerToolBehavior __instance, ref bool __result)
            {
                if (!__result) return;
                DiggerReport(__instance, false);
            }
        }

        [HarmonyPatch(typeof(MyInventory), "TransferItemsInternal")]
        private static class InventoryItemTransfer
        {
            public static void Postfix(MyInventory src, MyInventory dst, ref int amount)
            {
                if (src == dst) return;
                var srcPlayer = GetControllingPlayer(src.Entity);
                var dstPlayer = GetControllingPlayer(dst.Entity);

                if (srcPlayer != null && Holders.TryGetValue(srcPlayer.Id.SteamId, out var srcHolder))
                    srcHolder.ItemsLost.Inc(amount);
                if (dstPlayer != null && Holders.TryGetValue(dstPlayer.Id.SteamId, out var dstHolder))
                    dstHolder.ItemsAcquired.Inc(amount);
            }
        }

        private const long DamageScale = 100;

        [HarmonyPatch(typeof(MyDamageSystem), nameof(MyDamageSystem.RaiseAfterDamageApplied))]
        private static class DamageSystemDealt
        {
            public static void Postfix(MyEntity target, MyDamageInformation info)
            {
                var targetPlayer = GetControllingPlayer(target);
                var integerAmount = (long)(info.Amount * DamageScale);
                if (targetPlayer != null && Holders.TryGetValue(targetPlayer.Id.SteamId, out var targetHolder))
                    targetHolder.DamageTaken.Inc(integerAmount);
                var sourcePlayer = GetControllingPlayer(info.Attacker);
                if (sourcePlayer != null && Holders.TryGetValue(sourcePlayer.Id.SteamId, out var sourceHolder))
                    sourceHolder.DamageDealt.Inc(integerAmount);
            }
        }

        [HarmonyPatch(typeof(MyDamageSystem), nameof(MyDamageSystem.RaiseDestroyed))]
        private static class DamageSystemDestroyed
        {
            public static void Postfix(MyDamageInformation info)
            {
                var sourcePlayer = GetControllingPlayer(info.Attacker);
                if (sourcePlayer != null && Holders.TryGetValue(sourcePlayer.Id.SteamId, out var sourceHolder))
                    sourceHolder.DamageDestroyed.Inc();
            }
        }
        // ReSharper restore UnusedMember.Local
        // ReSharper restore InconsistentNaming
    }
}