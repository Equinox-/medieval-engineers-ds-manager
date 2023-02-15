using System.Collections.Generic;
using HarmonyLib;
using Medieval.Entities.Components.Planet;
using Medieval.GameSystems;
using Medieval.GameSystems.Building;
using Medieval.GameSystems.Tools;
using Meds.Wrapper.Group;
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
using Patches = Meds.Wrapper.Shim.Patches;

namespace Meds.Wrapper.Metrics
{
    public static class PlayerMetrics
    {
        private const string Players = "me.players";

        private static readonly Dictionary<ulong, PlayerMetricHolder> Holders = new Dictionary<ulong, PlayerMetricHolder>();

        public static void Register()
        {
            MyGridPlacer.OnBlockPlaced += OnBlockPlaced;
            MyGridPlacer.OnGridPlaced += OnGridPlaced;
            Patches.Patch(typeof(BlockRemoval));
            Patches.Patch(typeof(BuilderAction));
            Patches.Patch(typeof(DiggerToolCut));
            Patches.Patch(typeof(DiggerToolFill));
            Patches.Patch(typeof(InventoryItemTransfer));
            Patches.Patch(typeof(DamageSystemDealt));
            Patches.Patch(typeof(DamageSystemDestroyed));
        }

        public static void ReportNetwork(ulong steamId, long bytesSent = 0, long bytesReceived = 0, long? stateGroupCount = null, long? stateGroupDelay = null)
        {
            if (!Holders.TryGetValue(steamId, out var holder)) return;
            holder.ReportNetwork(bytesSent, bytesReceived, stateGroupCount, stateGroupDelay);
        }

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

        private sealed class PlayerMetricHolder
        {
            private readonly MetricGroup _group;

            private readonly Gauge _entityId;
            private readonly Gauge _areaFace;
            private readonly Gauge _areaX;
            private readonly Gauge _areaY;
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


            internal PlayerMetricHolder(ulong steamId)
            {
                _group = MetricRegistry.Group(MetricName.Of(Players, "steamId", steamId.ToString()));
                _entityId = _group.Gauge("entityId", double.NaN);
                _areaFace = _group.Gauge("areaFace", double.NaN);
                _areaX = _group.Gauge("areaX", double.NaN);
                _areaY = _group.Gauge("areaY", double.NaN);
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
            }

            private Counter _bytesSent, _bytesReceived;
            private Gauge _stateGroupCount, _stateGroupDelay;

            internal void ReportNetwork(long bytesSent = 0, long bytesReceived = 0, long? stateGroupCount = null, long? stateGroupDelay = null)
            {
                if (bytesSent > 0)
                {
                    if (_bytesSent == null) _bytesSent = _group.Counter("bytesSent");
                    _bytesSent.Inc(bytesSent);
                }

                if (bytesReceived > 0)
                {
                    if (_bytesReceived == null) _bytesReceived = _group.Counter("bytesReceived");
                    _bytesReceived.Inc(bytesReceived);
                }

                if (stateGroupCount != null)
                {
                    if (_stateGroupCount == null) _stateGroupCount = _group.Gauge("stateGroupCount", 0);
                    _stateGroupCount.SetValue(stateGroupCount.Value);
                }

                if (stateGroupDelay != null)
                {
                    if (_stateGroupDelay == null) _stateGroupDelay = _group.Gauge("stateGroupDelay", 0);
                    _stateGroupDelay.SetValue(stateGroupDelay.Value);
                }
            }

            internal void Update(MyPlayer player)
            {
                ulong? entityId = null;
                int? areaFace = null;
                Vector2D? areaXy = null;
                bool? areaPermitted = null;

                var entity = player.ControlledEntity;
                if (entity != null)
                {
                    entityId = entity.Id.Value;
                    var position = entity.GetPosition();
                    var planet = MyGamePruningStructureSandbox.GetClosestPlanet(position);
                    var areas = planet?.Get<MyPlanetAreasComponent>();
                    if (areas != null)
                    {
                        var localPos = position - planet.GetPosition();
                        MyEnvironmentCubemapHelper.ProjectToCube(ref localPos, out var face, out var texcoords);
                        areaFace = face;
                        var normXy = (texcoords + 1.0) * 0.5;
                        if (normXy.X >= 1.0)
                            normXy.X = 0.99999999;
                        if (normXy.Y >= 1.0)
                            normXy.Y = 0.99999999;
                        areaXy = normXy * areas.AreaCount;
                        var ownership = planet.Get<MyPlanetAreaOwnershipComponent>();
                        if (ownership != null)
                        {
                            var areaId = areas.GetArea(localPos);
                            var accessor = ownership.GetAreaPermissions(areaId);
                            areaPermitted = accessor.HasPermission(player.Identity.Id);
                        }
                    }
                }


                _entityId.SetValue(entityId ?? double.NaN);
                _areaFace.SetValue(areaFace ?? double.NaN);
                _areaX.SetValue(areaXy?.X ?? double.NaN);
                _areaY.SetValue(areaXy?.Y ?? double.NaN);
                _areaPermitted.SetValue(Gauge.ConvertValue(areaPermitted));
            }

            internal void Remove()
            {
                MetricRegistry.RemoveMetric(_group.Name);
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