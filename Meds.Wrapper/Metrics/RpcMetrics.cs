using System;
using System.Diagnostics;
using HarmonyLib;
using Meds.Metrics;
using Meds.Wrapper.Shim;
using VRage.Library.Collections;
using VRage.Network;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Metrics
{
    public static class RpcMetrics
    {
        private const string SeriesName = "me.profiler.rpc";

        public static void Register()
        {
            PatchHelper.Patch(typeof(CallSiteInvoke));
        }

        private static void Record(string mode, bool valid, string method, Type type, long dt, long bits)
        {
            var name = MetricName.Of(
                SeriesName,
                "mode", mode,
                "type", type?.Name ?? "unknown",
                "method", method,
                "valid", valid ? "true" : "false");
            MetricRegistry.PerTickTimerAdder(in name).Record(dt, bits);
        }

        [HarmonyPatch(typeof(MyReplicationLayer), "Invoke")]
        public static class CallSiteInvoke
        {
            public static void Prefix(out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            public static void Postfix(
                bool __result,
                long __state,
                CallSite callSite,
                BitStream stream)
            {
                var method = callSite.MethodInfo;
                var type = method?.DeclaringType;
                if (method == null)
                    return;
                var now = Stopwatch.GetTimestamp();
                var dt = now - __state;
                Record("invoke", __result, method.Name, type, dt, stream.BitLength);
            }
        }
    }
}