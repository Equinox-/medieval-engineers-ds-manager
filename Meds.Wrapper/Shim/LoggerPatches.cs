using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Engine.Physics;
using Sandbox.Game.EntityComponents.Character;
using VRage.Components;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Logging;
using ZLogger;
// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    public static class LoggerPatches
    {
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

        [HarmonyPatch(typeof(MyLog), "Init")]
        public static class PatchReplaceLogger
        {
            public static void Postfix(MyLog __instance)
            {
                Entrypoint.Instance.Services.GetService<ShimLog>().BindTo(__instance);
            }
        }

        [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(StringBuilder))]
        public static class PatchLogger1
        {
            public static bool Prefix(MyLog __instance, LogSeverity severity, StringBuilder builder)
            {
                __instance.Logger.Log(in ShimLog.ImpliedLoggerName, severity, builder);
                return false;
            }
        }

        [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(string))]
        public static class PatchLogger2
        {
            public static bool Prefix(MyLog __instance, LogSeverity severity, string message)
            {
                __instance.Logger.Log(in ShimLog.ImpliedLoggerName, severity, message);
                return false;
            }
        }

        [HarmonyPatch(typeof(MyLog), "Log", typeof(LogSeverity), typeof(string), typeof(object[]))]
        public static class PatchLogger3
        {
            public static bool Prefix(MyLog __instance, LogSeverity severity, string format, object[] args)
            {
                __instance.Logger.Log(in ShimLog.ImpliedLoggerName, severity,
                    FormattableStringFactory.Create(format, args));
                return false;
            }
        }

        [HarmonyPatch(typeof(MyLog), "WriteLineAndConsole", typeof(string))]
        public static class PatchLogger4
        {
            public static bool Prefix(MyLog __instance, string msg)
            {
                __instance.Logger.Log(in ShimLog.ImpliedLoggerName, LogSeverity.Info, msg);
                return false;
            }
        }
    }
}