using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Meds.Watchdog;
using Sandbox.Engine.Analytics;
using Sandbox.Engine.Physics;
using VRage.Logging;

// ReSharper disable RedundantAssignment
// ReSharper disable InconsistentNaming

namespace Meds.Standalone.Shim
{
    public static class Patches
    {
        private static readonly Harmony _harmony = new Harmony("meds.wrapper.core");

        public static void PatchAlways(bool late)
        {
            foreach (var type in typeof(Patches).Assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<AlwaysPatch>();
                if (attr == null || attr.Late != late) continue;
                Patch(type);
            }
        }
        
        public static void Patch(Type type)
        {
            _harmony.CreateClassProcessor(type).Patch();
        }

        private static readonly NamedLogger LoggerLegacy = new NamedLogger("Legacy", NullLogger.Instance);

        // [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(StringBuilder))]
        // public static class PatchLogger1
        // {
        //     public static bool Prefix(MyLog __instance, LogSeverity severity, StringBuilder builder)
        //     {
        //         __instance.Logger.Log(in LoggerLegacy, severity, builder);
        //         return false;
        //     }
        // }
        //
        // [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(string))]
        // public static class PatchLogger2
        // {
        //     public static bool Prefix(MyLog __instance, LogSeverity severity, string message)
        //     {
        //         __instance.Logger.Log(in LoggerLegacy, severity, message);
        //         return false;
        //     }
        // }
        //
        // [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(string), typeof(object[]))]
        // public static class PatchLogger3
        // {
        //     public static bool Prefix(MyLog __instance, LogSeverity severity, string format, object[] args)
        //     {
        //         __instance.Logger.Log(in LoggerLegacy, severity, FormattableStringFactory.Create(format, args));
        //         return false;
        //     }
        // }

        // No need to log process information since we track that with metrics.
        [HarmonyPatch(typeof(MyLog), "WriteProcessInformation")]
        [AlwaysPatch]
        public static class DisableProcessInfoLogging
        {
            public static bool Prefix() => false;
        }

        // No need to log physics information since we track that with metrics.
        [HarmonyPatch(typeof(MyPhysicsSandbox), "LogPhysics")]
        [AlwaysPatch]
        public static class DisablePhysicsLogging
        {
            public static bool Prefix() => false;
        }

        [HarmonyPatch(typeof(MyAnalyticsManager), nameof(MyAnalyticsManager.RegisterAnalyticsTracker))]
        [AlwaysPatch]
        public static class DisableAnalytics
        {
            public static bool Prefix() => false;
        }
    }
}