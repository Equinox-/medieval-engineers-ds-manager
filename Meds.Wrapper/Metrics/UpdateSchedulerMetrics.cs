using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Metrics;
using Meds.Shared;
using Meds.Wrapper.Shim;
using Sandbox.Engine.Networking;
using VRage.Components;
using VRage.Engine;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.GameServices;
using VRage.Network;

#pragma warning disable 618

namespace Meds.Wrapper.Metrics
{
    public static class UpdateSchedulerMetrics
    {
        private const string SeriesName = "me.profiler.scheduler";
        private const string FixedScheduler = "fixed";
        private const string TimedScheduler = "timed";
        private const string LegacyScheduler = "legacy";
        private const string MiscScheduler = "misc";

        private static bool _methodProfiling;
        private static bool _regionProfiling;

        public static void Register(MetricConfig config)
        {
            _methodProfiling = config.MethodProfiling;
            _regionProfiling = config.RegionProfiling;

            PatchHelper.Patch(typeof(TimedUpdatePatch));
            PatchHelper.Patch(typeof(FixedUpdatePatch));

            if (_methodProfiling)
            {
                PatchHelper.Patch(typeof(LegacyUpdateBefore));
                PatchHelper.Patch(typeof(LegacySimulate));
                PatchHelper.Patch(typeof(LegacyUpdateAfter));
                PatchHelper.Patch(typeof(MiscProfiler));
            }
        }

        private static IEnumerable<CodeInstruction> TranspileInternal(IEnumerable<CodeInstruction> instructions, MethodInfo profileMethod,
            MethodInfo invokeMethod)
        {
            var found = false;
            foreach (var i in instructions)
                if (i.Calls(invokeMethod))
                {
                    found = true;
                    yield return i.ChangeInstruction(OpCodes.Call, profileMethod);
                }
                else
                    yield return i;

            if (!found)
                FileLog.Log($"Failed to find call {invokeMethod.DeclaringType}#{invokeMethod.Name}.  Profiling of that won't work");
        }

        private static void Submit(string scheduler, MethodBase method, long start, object target = null)
        {
            Submit(scheduler, method.DeclaringType, method.Name, start, target);
        }

        private static void Submit(string scheduler, Type type, string method, long start, object target = null)
        {
            var now = Stopwatch.GetTimestamp();
            var dt = now - start;
            if (_methodProfiling)
            {
                var name = MetricName.Of(SeriesName,
                    "scheduler", scheduler,
                    "type", type?.Name ?? "unknown",
                    "method", method);
                MetricRegistry.PerTickTimer(in name).Record(dt);
            }

            if (_regionProfiling)
                RecordRegionUpdate(target, dt);
        }

        private static void RecordRegionUpdate(object target, long dt)
        {
            var geoData = target switch
            {
                MyEntity entity => entity.PositionComp?.WorldAABB.Center,
                MyEntityComponent entityComponent => entityComponent.Entity?.PositionComp?.WorldAABB.Center,
                _ => null
            };

            if (geoData != null)
                RegionMetrics.RecordRegionUpdateTime(geoData.Value, dt);
        }

        [HarmonyPatch(typeof(MyUpdateScheduler), "RunFixedUpdates")]
        private static class FixedUpdatePatch
        {
            private static void Profile(MyFixedUpdate update)
            {
                // MEC~460: Skip scheduled updates for unloaded entities due to multi-scheduling.
                if (update.Target is MyEntityComponent ec && !(ec.Entity?.InScene ?? false))
                    return;
                if (!_methodProfiling)
                {
                    update();
                    return;
                }

                // Don't double count legacy updates 
                var method = update.Method;
                if (method == DoSimulate || method == DoUpdateAfterSimulation || method == DoUpdateBeforeSimulation ||
                    method == ProfilingMetrics.RunSingleFrame)
                {
                    update();
                    return;
                }

                var start = Stopwatch.GetTimestamp();
                update();
                Submit(FixedScheduler, method, start, update.Target);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileInternal(instructions, AccessTools.Method(typeof(FixedUpdatePatch), nameof(Profile)),
                    AccessTools.Method(typeof(MyFixedUpdate), nameof(MyFixedUpdate.Invoke)));
        }

        [HarmonyPatch(typeof(MyUpdateScheduler), "RunTimedUpdates")]
        private static class TimedUpdatePatch
        {
            private static void Profile(MyTimedUpdate update, long dt)
            {
                // MEC~460: Skip scheduled updates for unloaded entities due to multi-scheduling.
                if (update.Target is MyEntityComponent ec && !(ec.Entity?.InScene ?? false))
                    return;
                if (!_methodProfiling)
                {
                    update(dt);
                    return;
                }

                var start = Stopwatch.GetTimestamp();
                update(dt);
                Submit(TimedScheduler, update.Method, start, update.Target);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileInternal(instructions, AccessTools.Method(typeof(TimedUpdatePatch), nameof(Profile)),
                    AccessTools.Method(typeof(MyTimedUpdate), nameof(MyTimedUpdate.Invoke)));
        }

        private static readonly Type LegacySchedulerType =
            Type.GetType("Sandbox.Game.SessionComponents.MySessionComponentLegacyUpdateScheduler, Sandbox.Game") ??
            throw new NullReferenceException("Failed to resolve MySessionComponentLegacyUpdateScheduler");

        private static readonly MethodBase DoUpdateBeforeSimulation = AccessTools.Method(LegacySchedulerType, "DoUpdateBeforeSimulation");
        private static readonly MethodBase DoSimulate = AccessTools.Method(LegacySchedulerType, "DoSimulate");
        private static readonly MethodBase DoUpdateAfterSimulation = AccessTools.Method(LegacySchedulerType, "DoUpdateAfterSimulation");

        [HarmonyPatch]
        private static class LegacyUpdateBefore
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return DoUpdateBeforeSimulation;
            }

            private static void Profile(MySessionComponentBase component)
            {
                var start = Stopwatch.GetTimestamp();
                component.UpdateBeforeSimulation();
                Submit(LegacyScheduler, component.GetType(), nameof(MySessionComponentBase.UpdateBeforeSimulation), start, component);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileInternal(instructions, AccessTools.Method(typeof(LegacyUpdateBefore), nameof(Profile)),
                    AccessTools.Method(typeof(MySessionComponentBase), nameof(MySessionComponentBase.UpdateBeforeSimulation)));
        }

        [HarmonyPatch]
        private static class LegacySimulate
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return DoSimulate;
            }

            private static void Profile(MySessionComponentBase component)
            {
                var start = Stopwatch.GetTimestamp();
                component.Simulate();
                Submit(LegacyScheduler, component.GetType(), nameof(MySessionComponentBase.Simulate), start, component);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileInternal(instructions, AccessTools.Method(typeof(LegacySimulate), nameof(Profile)),
                    AccessTools.Method(typeof(MySessionComponentBase), nameof(MySessionComponentBase.Simulate)));
        }

        [HarmonyPatch]
        private static class LegacyUpdateAfter
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return DoUpdateAfterSimulation;
            }

            private static void Profile(MySessionComponentBase component)
            {
                var start = Stopwatch.GetTimestamp();
                component.UpdateAfterSimulation();
                Submit(LegacyScheduler, component.GetType(), nameof(MySessionComponentBase.UpdateAfterSimulation), start, component);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                TranspileInternal(instructions, AccessTools.Method(typeof(LegacyUpdateAfter), nameof(Profile)),
                    AccessTools.Method(typeof(MySessionComponentBase), nameof(MySessionComponentBase.UpdateAfterSimulation)));
        }

        [HarmonyPatch]
        private static class MiscProfiler
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MyBlobTransmitter), nameof(MyBlobTransmitter.Update));
                yield return AccessTools.Method(typeof(MyReplicationServer), nameof(MyReplicationServer.Update));
                yield return AccessTools.Method(typeof(MyGameService), nameof(MyGameService.Update));
                yield return AccessTools.Method(Type.GetType("Sandbox.Engine.Multiplayer.MyTransportLayer, Sandbox.Game"), "Tick");
                yield return AccessTools.Method(Type.GetType("Sandbox.Engine.Networking.MyReceiveQueue, Sandbox.Game"), "Process");
            }

            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            public static void Postfix(long __state, MethodBase __originalMethod)
            {
                Submit(MiscScheduler, __originalMethod, __state);
            }
        }
    }
}