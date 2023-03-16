using System;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Engine.Physics;
using VRage.Components;
using VRage.Game.Components;
using VRage.Logging;
using ZLogger;

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
                __instance.Logger.Log(in ShimLog.ImpliedLoggerName, severity, FormattableStringFactory.Create(format, args));
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

        [HarmonyPatch(typeof(MyUpdateScheduler), "ReportError")]
        public static class UpdateSchedulerError
        {
            public static void Prefix(Delegate action, Exception error)
            {
                void Report<T>(T payload)
                {
                    Entrypoint
                        .LoggerFor(action.Method.DeclaringType ?? typeof(UpdateSchedulerError))
                        .ZLogErrorWithPayload(error, payload, "Update method failed: {0} on {1}", action.Method.FullDescription(), action.Target ?? "null");
                }

                switch (action.Target)
                {
                    case MyEntityComponent ec:
                        Report(new EntityComponentPayload(ec, action.Method.Name));
                        return;
                    case IComponent c:
                        Report(new ComponentPayload(c, action.Method.Name));
                        return;
                    default:
                        Report(new MemberPayload(action.Method));
                        return;
                }
            }
        }
    }
}