using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Medieval.GameSystems;
using Medieval.GameSystems.Building;
using Medieval.GUI;
using Medieval.World.Persistence;
using Meds.Shared;
using Meds.Wrapper.Shim;
using Meds.Wrapper.Trace;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems.Chat;
using Sandbox.Game.Players;
using Sandbox.Game.SessionComponents.Clipboard;
using VRage.Components.Entity.CubeGrid;
using VRage.Entity.Block;
using VRage.Game;
using VRage.Game.Components;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Inventory;
using VRage.Scene;
using VRage.Session;
using VRageMath;
using MySession = Sandbox.Game.World.MySession;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    public static class MedievalMasterAudit
    {
        private class MedievalMasterSession
        {
            public readonly TraceState Trace;
            private readonly string _sessionId;
            private readonly MyPlayer _player;
            private TraceSpan _span;
            private readonly DateTime _start;
            private readonly StringBuilder _log = new StringBuilder();
            private long _lastAreaId;
            private long _throttleUntil;
            private bool _hasFlown;
            private bool _hasSpectated;
            private string _name;
            private static readonly long ThrottleTime = Stopwatch.Frequency * 15;

            private TraceSpan? _flying;
            private TraceSpan? _spectating;

            public MedievalMasterSession(MyPlayer player)
            {
                Trace = TraceState.NewTrace();
                _player = player;
                _span = Trace.StartSpan($"Medieval Master by {player.Identity?.DisplayName ?? player.Id.SteamId.ToString()}");
                _sessionId = $"mm-audit-{_span.SpanId:X8}";
                _start = DateTime.UtcNow;
            }

            private string Elapsed
            {
                get
                {
                    var elapsed = DateTime.UtcNow - _start;
                    return $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
                }
            }

            private bool UpdateFlying()
            {
                var flying = _player.ControlledEntity?.Get<MyFlightComponent>()?.Enabled ?? false;
                if (_flying.HasValue == flying) return false;
                if (flying)
                {
                    _flying = Trace.StartSpan("Flying");
                    if (_hasFlown) return false;
                    StartLine().Append("Used Flight");
                    _hasFlown = true;
                    return true;
                }

                _flying.FinishAndClear();
                return false;
            }

            private bool UpdateSpectator()
            {
                const double SpectatorEnableDistance = 100;

                var pos = _player.ControlledEntity?.GetPosition();
                if (!pos.HasValue) return false;
                if (!RpcClientStateHolder.TryGetState(_player.Id, out var clientState)) return false;
                var spectating = Vector3D.DistanceSquared(clientState.Position, pos.Value) >= SpectatorEnableDistance * SpectatorEnableDistance;
                if (spectating == _spectating.HasValue) return false;
                if (spectating)
                {
                    _spectating = Trace.StartSpan("Spectating");
                    if (_hasSpectated) return false;
                    StartLine().Append("Used Spectator");
                    _hasSpectated = true;
                    return true;
                }

                _spectating.FinishAndClear();
                return false;
            }

            private bool PollingUpdate()
            {
                var notify = false;
                notify |= LogArea();
                notify |= UpdateFlying();
                notify |= UpdateSpectator();
                return notify;
            }

            private StringBuilder StartLine()
            {
                if (_log.Length != 0) _log.AppendLine();
                _log.Append(Elapsed).Append(" ");
                return _log;
            }

            public void Finish()
            {
                PollingUpdate();
                _flying.FinishAndClear();
                _spectating.FinishAndClear();
                _span.Finish();
                Notify(finish: true);
            }

            private bool LogArea()
            {
                var pos = RpcClientStateHolder.TryGetState(_player.Id, out var clientState)
                    ? clientState.Position
                    : _player.ControlledEntity?.GetPosition();
                if (pos == null) return false;
                var planet = MyGamePruningStructureSandbox.GetClosestPlanet(pos.Value);
                var areas = planet?.Get<MyPlanetAreasComponent>();
                if (areas == null) return false;
                var localPos = pos.Value - planet.GetPosition();
                var areaId = areas.GetArea(localPos);
                if (areaId == _lastAreaId) return false;
                _lastAreaId = areaId;
                MyPlanetAreasComponent.UnpackAreaId(areaId, out string king, out var region, out var area);
                var sb = StartLine().Append("Entered Area ").Append(king).Append(" ").Append(region).Append(", ").Append(area);
                if (_flying.HasValue) sb.Append(" [F]");
                if (_spectating.HasValue) sb.Append(" [S]");
                return true;
            }

            private void Notify(bool finish = false)
            {
                if (!finish && Stopwatch.GetTimestamp() < _throttleUntil) return;
                var builder = MedsModApi.SendModEvent("meds.audit.medievalMaster", MedsAppPackage.Instance);
                builder.SetReuseIdentifier(_sessionId, TimeSpan.FromHours(1));
                _name = _player.Identity?.DisplayName ?? _name ?? _player.Id.SteamId.ToString();
                var traceInfo = string.Format(Entrypoint.Config?.Runtime?.Current?.Audit?.TraceIdFormat ?? AuditConfig.DefaultTraceIdFormat, Trace.TraceId);
                builder.SetMessage(
                    $"{(finish ? $"{_name} used Medieval Master for {Elapsed}" : $"{_name} started using Medieval Master {_start.AsDiscordTime(DiscordTimeFormat.Relative)}")} {traceInfo}\n```\n{_log}\n```");
                builder.Send();
                _throttleUntil = Stopwatch.GetTimestamp() + ThrottleTime;
            }

            public UpdateToken Update() => new UpdateToken(this);

            public struct UpdateToken : IDisposable
            {
                private readonly MedievalMasterSession _session;
                private bool _notify;

                public UpdateToken(MedievalMasterSession session)
                {
                    _session = session;
                    _notify = session.PollingUpdate();
                }

                public StringBuilder StartLine()
                {
                    _notify = true;
                    return _session.StartLine();
                }

                public void Dispose()
                {
                    if (_notify) _session.Notify();
                }
            }
        }

        private static readonly Dictionary<ulong, MedievalMasterSession> MedievalMasterSessions = new Dictionary<ulong, MedievalMasterSession>();

        internal static bool TryGetTrace(ulong id, out TraceState state)
        {
            state = MedievalMasterSessions.GetValueOrDefault(id)?.Trace;
            return state != null;
        }

        internal static void PlayerLeft(ulong id)
        {
            if (MedievalMasterSessions.TryGetValue(id, out var session))
                session.Finish();
            MedievalMasterSessions.Remove(id);
        }

        internal static void RegularUpdate()
        {
            foreach (var session in MedievalMasterSessions.Values)
                session.Update().Dispose();
        }

        internal static void Shutdown()
        {
            foreach (var session in MedievalMasterSessions.Values)
                session.Finish();
            MedievalMasterSessions.Clear();
        }

        [HarmonyPatch(typeof(MySession), "OnAdminModeEnabled")]
        [AlwaysPatch]
        public static class AuditMmStartStop
        {
            public static void Postfix()
            {
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;
                var wasEnabled = MySession.Static?.IsAdminModeEnabled(player.Id.SteamId) ?? false;
                MedievalMasterSession session;
                if (wasEnabled && !MedievalMasterSessions.ContainsKey(player.Id.SteamId))
                {
                    MedievalMasterSessions.Add(player.Id.SteamId, session = new MedievalMasterSession(player));
                    session.Update().Dispose();
                }

                AuditPayload.Create(
                        wasEnabled ? AuditEvent.MedievalMasterStart : AuditEvent.MedievalMasterStop,
                        player)
                    .Emit();

                if (!wasEnabled && MedievalMasterSessions.TryGetValue(player.Id.SteamId, out session))
                {
                    session.Finish();
                    MedievalMasterSessions.Remove(player.Id.SteamId);
                }
            }
        }

        [HarmonyPatch]
        [AlwaysPatch]
        public static class AuditPlaceGrids
        {
            public static IEnumerable<MethodBase> TargetMethods() => new[]
            {
                AccessTools.Method(typeof(MyGridPlacer), "ProcessPasting"),
                AccessTools.Method(typeof(MyGridPlacer), "ProcessBlockPlacement")
            }.Where(x => x != null);

            public static void Postfix(MyGridPlacer.MyBuildServerRequest buildRequest, List<MyGridPlacer.MergeScene> createdScenes)
            {
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;
                var clipboard = new ClipboardOpPayload();
                var bounds = BoundingBoxD.CreateInvalid();
                foreach (var scene in createdScenes)
                foreach (var grid in scene.Grids)
                {
                    clipboard.Add(grid);
                    bounds.Include(grid.Container.Get<MyPositionComponentBase>().WorldAABB);
                }

                if (clipboard.Grids == 0)
                    return;

                var payload = AuditPayload
                    .Create(buildRequest.Pasting ? AuditEvent.ClipboardPaste : AuditEvent.BlocksPlace, player, owningLocation: bounds.Center)
                    .ClipboardOpPayload(in clipboard);

                var clipboardInfo = payload.ClipboardOp;
                if (clipboardInfo.HasValue && MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    var msg = update.StartLine().Append(buildRequest.Pasting ? "Pasted " : "Placed ");
                    if (clipboard.Grids > 1)
                        msg.Append(clipboard.Grids).Append(" grids with ");
                    msg.Append(clipboard.Blocks);
                    if (clipboard.BlockSubtype != null)
                        msg.Append(" ").Append(clipboard.BlockSubtype);
                    msg.Append(" blocks");
                }

                payload.Emit();
            }
        }

        [HarmonyPatch(typeof(MyClipboardComponent), "OnGridClosedRequest")]
        [AlwaysPatch]
        public static class AuditCutGrids
        {
            public static void Prefix(EntityId entityId)
            {
                var scene = MySession.Static.Scene;
                if (!scene.TryGetEntity(entityId, out var entity))
                    return;
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;

                var clipboard = new ClipboardOpPayload();
                var bounds = BoundingBoxD.CreateInvalid();

                var collector = scene.GetCollector();
                collector.CollectAllConnectedEntities(entity);

                foreach (var attached in collector.Entities)
                {
                    clipboard.Add(attached);
                    bounds.Include(attached.PositionComp.WorldAABB);
                }

                if (clipboard.Grids == 0)
                    return;

                var payload = AuditPayload.Create(AuditEvent.ClipboardCut, player, owningLocation: bounds.Center)
                    .ClipboardOpPayload(in clipboard);

                var clipboardInfo = payload.ClipboardOp;
                if (clipboardInfo.HasValue && MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    update.StartLine().Append("Cut ")
                        .Append(clipboardInfo.Value.Grids).Append(" grids with ")
                        .Append(clipboardInfo.Value.Blocks).Append(" blocks");
                }

                payload.Emit();
            }
        }

        [HarmonyPatch(typeof(MyGridPlacer), nameof(MyGridPlacer.Server_RemoveRequest))]
        [AlwaysPatch]
        public static class AuditRemoveBlocks
        {
            public struct RemoveBlocksState
            {
                public MyGridDataComponent Grid;
                public Vector3D Pos;
                public ClipboardOpPayload Blocks;
            }

            public static void Prefix(EntityId gridEntityId, BlockId blockId, out RemoveBlocksState __state)
            {
                __state = default;
                var scene = MySession.Static.Scene;
                if (!scene.TryGetEntity(gridEntityId, out var entity)
                    || !entity.Components.TryGet(out __state.Grid)
                    || !__state.Grid.TryGetBlock(blockId, out var block))
                    return;

                __state.Pos = __state.Grid.GetBlockWorldBounds(block).Center;
                __state.Blocks.Grids = 0;
                __state.Blocks.Add(block);
            }

            public static void Postfix(BlockId blockId, in RemoveBlocksState __state)
            {
                if (__state.Grid == null || __state.Grid.TryGetBlock(blockId, out _) || __state.Blocks.Blocks == 0)
                    return;
                EmitEvent(in __state);
            }

            public static void EmitEvent(in RemoveBlocksState state)
            {
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;

                var payload = AuditPayload.Create(AuditEvent.BlocksRemove, player, owningLocation: state.Pos)
                    .ClipboardOpPayload(in state.Blocks);

                var clipboardInfo = payload.ClipboardOp;
                if (clipboardInfo.HasValue && MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    var msg = update.StartLine().Append("Removed ");
                    if (state.Blocks.Grids > 1) msg.Append(state.Blocks.Grids).Append(" grids with ");
                    msg.Append(state.Blocks.Blocks);
                    if (state.Blocks.BlockSubtype != null) msg.Append(" ").Append(state.Blocks.BlockSubtype);
                    msg.Append(" blocks");
                }

                payload.Emit();
            }
        }

        [HarmonyPatch(typeof(MyGridPlacer), nameof(MyGridPlacer.Server_RemoveRequestMultiple))]
        [AlwaysPatch]
        public static class AuditRemoveMultipleBlocks
        {
            public static void Prefix(EntityId gridEntityId, List<BlockId> blocksToRemove, out AuditRemoveBlocks.RemoveBlocksState __state)
            {
                __state = default;
                var scene = MySession.Static.Scene;
                if (!scene.TryGetEntity(gridEntityId, out var entity) || !entity.Components.TryGet(out __state.Grid))
                    return;

                __state.Blocks.Grids = 0;
                var pos = Vector3D.Zero;
                foreach (var id in blocksToRemove)
                    if (__state.Grid.TryGetBlock(id, out var block))
                    {
                        __state.Blocks.Add(block);
                        pos += __state.Grid.GetBlockWorldBounds(block).Center;
                    }

                if (__state.Blocks.Blocks == 0) return;

                __state.Pos = pos / __state.Blocks.Blocks;
            }

            public static void Postfix(List<BlockId> blocksToRemove, ref AuditRemoveBlocks.RemoveBlocksState __state)
            {
                if (__state.Grid == null || __state.Blocks.Blocks == 0) return;
                var removedBlocks = 0;
                foreach (var id in blocksToRemove)
                    if (!__state.Grid.TryGetBlock(id, out _))
                        removedBlocks++;
                if (removedBlocks == 0) return;
                __state.Blocks.Blocks = Math.Min(__state.Blocks.Blocks, removedBlocks);
                AuditRemoveBlocks.EmitEvent(in __state);
            }
        }

        [HarmonyPatch(typeof(MyFlightComponent), nameof(MyFlightComponent.Enable))]
        [AlwaysPatch]
        public static class AuditFlightStart
        {
            public static void Postfix(MyFlightComponent __instance)
            {
                if (!__instance.Enabled) return;
                var player = MyPlayers.Static?.GetControllingPlayer(__instance.Entity);
                if (player == null) return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                    session.Update().Dispose();

                AuditPayload.Create(AuditEvent.FlyingStart, player).Emit();
            }
        }

        [HarmonyPatch(typeof(MyFlightComponent), nameof(MyFlightComponent.Disable))]
        [AlwaysPatch]
        public static class AuditFlightEnd
        {
            public static void Postfix(MyFlightComponent __instance)
            {
                if (__instance.Enabled) return;
                var player = MyPlayers.Static?.GetControllingPlayer(__instance.Entity);
                if (player == null) return;

                AuditPayload.Create(AuditEvent.FlyingEnd, player).Emit();

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                    session.Update().Dispose();
            }
        }

        [HarmonyPatch(typeof(MySpawnItemScreen), "RequestAddItem")]
        [AlwaysPatch]
        public static class AuditSpawnItemsInInventory
        {
            public static void Postfix(SerializableDefinitionId itemId, int amount)
            {
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    update.StartLine().Append("Spawn ").Append(itemId.SubtypeName).Append(" (x").Append(amount).Append(")");
                }

                AuditPayload.Create(AuditEvent.SpawnItems, player)
                    .InventoryOpPayload(new InventoryOpPayload { Subtype = itemId.SubtypeId, Amount = amount })
                    .Emit();
            }
        }

        [HarmonyPatch(typeof(MyFloatingObjects), "RequestSpawnCreative_Implementation")]
        [AlwaysPatch]
        public static class AuditSpawnItemsInWorld
        {
            public static void Postfix(MyObjectBuilder_FloatingObject obj)
            {
                if (obj.Item == null) return;
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    update.StartLine().Append("Spawn ").Append(obj.Item.SubtypeName).Append(" (x").Append(obj.Item.Amount).Append(")");
                }

                AuditPayload.Create(AuditEvent.SpawnItems, player)
                    .InventoryOpPayload(new InventoryOpPayload { Subtype = obj.Item.SubtypeName, Amount = obj.Item.Amount })
                    .Emit();
            }
        }

        [HarmonyPatch(typeof(MyInventory), "AddItems_Request")]
        [AlwaysPatch]
        public static class AuditAddItemsOnInventory
        {
            public static void Postfix(MyObjectBuilder_InventoryItem itemBuilder)
            {
                if (itemBuilder == null || MyEventContext.Current.HasValidationFailed) return;
                var player = AuditPayload.GetActingPlayer();
                if (player == null) return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    update.StartLine().Append("Spawn ").Append(itemBuilder.SubtypeName).Append(" (x").Append(itemBuilder.Amount).Append(")");
                }

                AuditPayload.Create(AuditEvent.SpawnItems, player)
                    .InventoryOpPayload(new InventoryOpPayload { Subtype = itemBuilder.SubtypeName, Amount = itemBuilder.Amount })
                    .Emit();
            }
        }

        [HarmonyPatch(typeof(MyChatSystem), nameof(MyChatSystem.RegisterChatCommand))]
        [AlwaysPatch]
        public static class AuditChatCommand
        {
            private static void AuditLog(string key, ulong sender, string message, Exception failure)
            {
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(sender));
                if (player == null)
                    return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    using var update = session.Update();
                    var sb = update.StartLine().Append("Used Command ").Append(message);
                    if (failure != null)
                        sb.Append(", Failed: ").Append(failure.Message);
                }

                AuditPayload.Create(AuditEvent.ChatCommand, player)
                    .ChatCommandPayload(new ChatCommandPayload
                    {
                        Prefix = key,
                        Command = message,
                    })
                    .Emit();
            }

            public static void Prefix(string key, ref MyChatSystem.HandleMessage messageHandler)
            {
                var original = messageHandler;
                messageHandler = (sender, message, type) =>
                {
                    var result = false;
                    Exception failure = null;
                    try
                    {
                        return result = original(sender, message, type);
                    }
                    catch (Exception err)
                    {
                        failure = err;
                        throw;
                    }
                    finally
                    {
                        if (failure != null || result)
                            AuditLog(key, sender, message, failure);
                    }
                };
            }
        }
    }
}