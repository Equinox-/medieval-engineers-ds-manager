using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Engine.Analytics;
using Sandbox.Engine.Physics;
using VRage.Logging;

// ReSharper disable RedundantAssignment
// ReSharper disable InconsistentNaming

namespace Meds.Standalone.Shim
{
    public static class Patches
    {
        private static Harmony _harmony;

        public static void Patch()
        {
            _harmony = new Harmony("meds.wrapper.core");
            AccessTools.GetTypesFromAssembly(typeof(Patches).Assembly)
                .Where(x => x.GetCustomAttribute<PatchLateAttribute>() == null)
                .Do(type => _harmony.CreateClassProcessor(type).Patch());
        }

        public static void PatchLate()
        {
            AccessTools.GetTypesFromAssembly(typeof(Patches).Assembly)
                .Where(x => x.GetCustomAttribute<PatchLateAttribute>() != null)
                .Do(type => _harmony.CreateClassProcessor(type).Patch());
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
        public static class PatchLogger4
        {
            public static bool Prefix()
            {
                return false;
            }
        }

        // No need to log physics information since we track that with metrics.
        [HarmonyPatch(typeof(MyPhysicsSandbox), "LogPhysics")]
        public static class PatchPhysics
        {
            public static bool Prefix()
            {
                return false;
            }
        }

        [HarmonyPatch(typeof(MyAnalyticsManager), nameof(MyAnalyticsManager.RegisterAnalyticsTracker))]
        public static class DisableAnalytics
        {
            public static bool Prefix()
            {
                return false;
            }
        }
    }
}