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
            private static readonly long SlowTickStartupDelay = Stopwatch.Frequency * 600; // 10 minutes
            private static readonly long SlowTickSpacing = Stopwatch.Frequency * 300; // 5 minutes
            private static readonly double MillisPerTick = 1000.0 / Stopwatch.Frequency;
            private static readonly long MinSlowTickDuration = Stopwatch.Frequency * 5 / 1000; // 5ms

            private static long? _nextSlowTickMessage;

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
                if (dt < MinSlowTickDuration)
                    return;
                var nextSlowTick = _nextSlowTickMessage ??= now + SlowTickStartupDelay;
                if (now < nextSlowTick)
                    return;
                var p99 = TickTimer.Percentile(.99);
                if (dt <= p99)
                    return;
                Entrypoint.LoggerFor(typeof(GameTickProfiler)).ZLogWarning(
                    "Tick was slower than 99% of ticks (tick={0} ms, p90={1} ms, p95={2} ms, p99={3} ms, max={4} ms)",
                    dt * MillisPerTick,
                    TickTimer.Percentile(.9) * MillisPerTick,
                    TickTimer.Percentile(.95) * MillisPerTick,
                    p99 * MillisPerTick,
                    TickTimer.Percentile(1) * MillisPerTick);
                _nextSlowTickMessage = now + SlowTickSpacing;
            }
        }
    }
}