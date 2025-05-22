using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Medieval.GameSystems;
using Medieval.GameSystems.Building;
using Medieval.GUI;
using Meds.Shared;
using Meds.Wrapper.Shim;
using Meds.Wrapper.Trace;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Players;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Components;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.Scene;
using VRageMath;

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
            private static readonly long ThrottleTime = Stopwatch.Frequency * 15;

            public MedievalMasterSession(MyPlayer player)
            {
                Trace = TraceState.NewTrace();
                _player = player;
                _span = Trace.StartSpan($"Medieval Master by {player.Identity?.DisplayName ?? player.Id.SteamId.ToString()}");
                _sessionId = $"mm-audit-{_span.SpanId:X8}";
                _start = DateTime.UtcNow;
                _log.Append("```\n").Append("Trace: ").Append(Trace.TraceId);
            }

            private string Elapsed
            {
                get
                {
                    var elapsed = DateTime.UtcNow - _start;
                    return $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
                }
            }

            private TraceSpan? _flying;

            public void SetFlying(bool flying)
            {
                if (_flying.HasValue == flying) return;
                if (flying)
                {
                    _flying = Trace.StartSpan("Flying");
                    StartLine().Append("Started Flying");
                }
                else
                {
                    StartLine().Append("Stopped Flying");
                    var fly = _flying.Value;
                    _flying = null;
                    fly.Finish();
                }

                Notify();
            }

            public StringBuilder StartLine()
            {
                LogArea();
                if (_log.Length != 0) _log.AppendLine();
                _log.Append(Elapsed).Append(" ");
                return _log;
            }

            public void Begin()
            {
                if (_player.ControlledEntity?.Get<MyFlightComponent>()?.Enabled ?? false)
                    SetFlying(true);
                Notify();
            }

            public void Finish()
            {
                if (_flying.HasValue)
                {
                    var fly = _flying.Value;
                    _flying = null;
                    fly.Finish();
                }

                _span.Finish();
                Notify(finish: true);
            }

            private void LogArea()
            {
                var pos = _player.ControlledEntity?.GetPosition();
                if (pos == null) return;
                var planet = MyGamePruningStructureSandbox.GetClosestPlanet(pos.Value);
                var areas = planet?.Get<MyPlanetAreasComponent>();
                if (areas == null) return;
                var localPos = pos.Value - planet.GetPosition();
                var areaId = areas.GetArea(localPos);
                if (areaId == _lastAreaId) return;
                _lastAreaId = areaId;
                MyPlanetAreasComponent.UnpackAreaId(areaId, out string king, out var region, out var area);
                StartLine().Append("Entered Area ").Append(king).Append(" ").Append(region).Append(", ").Append(area);
            }

            public void Notify(bool finish = false)
            {
                if (!finish && Stopwatch.GetTimestamp() < _throttleUntil) return;

                LogArea();
                var builder = MedsModApi.SendModEvent("MedievalMasterAudit", MedsAppPackage.Instance);
                builder.SetReuseIdentifier(_sessionId, TimeSpan.FromHours(1));
                var name = _player.Identity?.DisplayName ?? _player.Id.SteamId.ToString();
                builder.SetMessage(
                    $"{(finish ? $"{name} used Medieval Master for {Elapsed}" : $"{name} started using Medieval Master {_start.AsDiscordTime(DiscordTimeFormat.Relative)}")}\n{_log}\n```");
                builder.Send();
                _throttleUntil = Stopwatch.GetTimestamp() + ThrottleTime;
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

        [HarmonyPatch(typeof(MySession), "OnAdminModeEnabled")]
        [AlwaysPatch]
        public static class AuditMmStartStop
        {
            public static void Postfix()
            {
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var wasEnabled = MySession.Static?.IsAdminModeEnabled(userId) ?? false;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;
                MedievalMasterSession session;
                if (wasEnabled && !MedievalMasterSessions.ContainsKey(userId))
                {
                    MedievalMasterSessions.Add(userId, session = new MedievalMasterSession(player));
                    session.Begin();
                }

                AuditPayload.Create(
                        wasEnabled ? AuditEvent.MedievalMasterStart : AuditEvent.MedievalMasterStop,
                        player)
                    .Emit();

                if (!wasEnabled && MedievalMasterSessions.TryGetValue(userId, out session))
                {
                    session.Finish();
                    MedievalMasterSessions.Remove(userId);
                }
            }
        }

        [HarmonyPatch(typeof(MyGridPlacer), "ProcessPasting")]
        [AlwaysPatch]
        public static class AuditPasteGrids
        {
            public static void Postfix(List<MyGridPlacer.MergeScene> createdScenes)
            {
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;
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

                var payload = AuditPayload.Create(AuditEvent.ClipboardPaste, player, owningLocation: bounds.Center)
                    .ClipboardOpPayload(in clipboard);

                var clipboardInfo = payload.ClipboardOp;
                if (clipboardInfo.HasValue && MedievalMasterSessions.TryGetValue(userId, out var session))
                {
                    session.StartLine().Append("Pasted ")
                        .Append(clipboardInfo.Value.Grids).Append(" grids with ")
                        .Append(clipboardInfo.Value.Blocks).Append(" blocks");
                    session.Notify();
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
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;

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
                if (clipboardInfo.HasValue && MedievalMasterSessions.TryGetValue(userId, out var session))
                {
                    session.StartLine().Append("Cut ")
                        .Append(clipboardInfo.Value.Grids).Append(" grids with ")
                        .Append(clipboardInfo.Value.Blocks).Append(" blocks");
                    session.Notify();
                }

                payload.Emit();
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
                    session.SetFlying(true);

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

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                    session.SetFlying(false);

                AuditPayload.Create(AuditEvent.FlyingEnd, player).Emit();
            }
        }

        [HarmonyPatch(typeof(MySpawnItemScreen), "RequestAddItem")]
        [AlwaysPatch]
        public static class AuditSpawnItemsInInventory
        {
            public static void Postfix(SerializableDefinitionId itemId, int amount)
            {
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    session.StartLine().Append("Spawn ").Append(itemId.SubtypeName).Append(" (x").Append(amount).Append(")");
                    session.Notify();
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
                var userId = MyEventContext.Current.IsLocallyInvoked ? Sync.MyId : MyEventContext.Current.Sender.Value;
                var player = MyPlayers.Static?.GetPlayer(new MyPlayer.PlayerId(userId));
                if (player == null)
                    return;

                if (MedievalMasterSessions.TryGetValue(player.Id.SteamId, out var session))
                {
                    session.StartLine().Append("Spawn ").Append(obj.Item.SubtypeName).Append(" (x").Append(obj.Item.Amount).Append(")");
                    session.Notify();
                }

                AuditPayload.Create(AuditEvent.SpawnItems, player)
                    .InventoryOpPayload(new InventoryOpPayload { Subtype = obj.Item.SubtypeName, Amount = obj.Item.Amount })
                    .Emit();
            }
        }
    }
}