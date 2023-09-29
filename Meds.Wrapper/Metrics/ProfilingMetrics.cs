using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Meds.Metrics;
using Meds.Wrapper.Shim;
using Sandbox.Engine.Platform;
using VRage.Session;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Metrics
{
    public class ProfilingMetrics
    {
        private static readonly MetricName GameTickSeries = MetricName.Of("me.profiler.tick");
        private static readonly MetricName SaveSeriesBase = MetricName.Of("me.profiler.save");


        internal static readonly MethodBase RunSingleFrame = AccessTools.Method(typeof(Game), "RunSingleFrame");

        [HarmonyPatch(typeof(MySessionPersistence), "GetSnapshot")]
        [AlwaysPatch]
        private static class SaveSnapshotProfiler
        {
            private static readonly Timer _saveSnapshot = MetricRegistry.Timer(SaveSeriesBase.WithSuffix(".snapshot"));

            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            public static void Postfix(long __state)
            {
                var now = Stopwatch.GetTimestamp();
                var dt = now - __state;
                _saveSnapshot.Record(dt);
            }
        }

        [HarmonyPatch(typeof(MySessionPersistence), "Apply")]
        [AlwaysPatch]
        private static class SaveApplyProfiler
        {
            private static readonly Timer _saveSnapshot = MetricRegistry.Timer(SaveSeriesBase.WithSuffix(".apply"));

            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            public static void Postfix(long __state)
            {
                var now = Stopwatch.GetTimestamp();
                var dt = now - __state;
                _saveSnapshot.Record(dt);
            }
        }

        [HarmonyPatch]
        [AlwaysPatch]
        private static class GameTickProfiler
        {
            private static readonly Timer TickTimer = MetricRegistry.Timer(in GameTickSeries);

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return RunSingleFrame;
            }

            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            public static void Postfix(long __state)
            {
                var now = Stopwatch.GetTimestamp();
                var dt = now - __state;
                TickTimer.Record(dt);
            }
        }
    }
}