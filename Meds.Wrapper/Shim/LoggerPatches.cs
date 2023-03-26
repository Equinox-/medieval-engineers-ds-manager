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
using VRage.Components;
using VRage.Game.Components;
using VRage.Game.Entity;
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

        [HarmonyPatch(typeof(MyUpdateScheduler), "ReportError")]
        public static class UpdateSchedulerError
        {
            public static void Prefix(Delegate action, Exception error)
            {
                void Report<T>(T payload)
                {
                    Entrypoint
                        .LoggerFor(action.Method.DeclaringType ?? typeof(UpdateSchedulerError))
                        .ZLogErrorWithPayload(error, payload, "Update method failed: {0} on {1}",
                            action.Method.FullDescription(), action.Target ?? "null");
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


        [HarmonyPatch]
        [AlwaysPatch]
        public static class EntityComponentErrors
        {
            private static void Report(
                string function,
                MyEntityComponent ec,
                MyEntityComponentContainer container,
                Exception error)
            {
                Entrypoint
                    .LoggerFor(ec.GetType())
                    .ZLogErrorWithPayload(error,
                        new EntityComponentPayload(ec, null, container),
                        "Failed to invoke {0} on {1}",
                        function,
                        container?.Entity?.ToString());
            }

            private static void OnAddedToScene(MyEntityComponent ec, MyEntityComponentContainer container)
            {
                try
                {
                    ec.OnAddedToScene();
                }
                catch (Exception error)
                {
                    Report("OnAddedToScene", ec, container, error);
                    throw;
                }
            }

            private static void OnRemovedFromScene(MyEntityComponent ec, MyEntityComponentContainer container)
            {
                try
                {
                    ec.OnRemovedFromScene();
                }
                catch (Exception error)
                {
                    Report("OnRemovedFromScene", ec, container, error);
                    throw;
                }
            }

            private static void OnAddedToContainer(MyEntityComponent ec, MyEntityComponentContainer container)
            {
                try
                {
                    ec.OnAddedToContainer();
                }
                catch (Exception error)
                {
                    Report("OnAddedToContainer", ec, container, error);
                    throw;
                }
            }

            private static void OnBeforeRemovedFromContainer(MyEntityComponent ec, MyEntityComponentContainer container)
            {
                try
                {
                    ec.OnBeforeRemovedFromContainer();
                }
                catch (Exception error)
                {
                    Report("OnBeforeRemovedFromContainer", ec, container, error);
                    throw;
                }
            }


            private static readonly Dictionary<MethodInfo, MethodInfo> AliasedMethods = new[]
            {
                "OnAddedToScene",
                "OnRemovedFromScene",
                "OnAddedToContainer",
                "OnBeforeRemovedFromContainer"
            }.ToDictionary(
                x => AccessTools.Method(typeof(MyEntityComponent), x),
                x => AccessTools.Method(typeof(EntityComponentErrors), x));

            public static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(MyEntityComponentContainer), "OnAddedToScene");
                yield return AccessTools.Method(typeof(MyEntityComponentContainer), "OnRemovedFromScene");
                yield return AccessTools.Method(typeof(MyEntityComponent), "SetContainer");
            }

            public static IEnumerable<CodeInstruction> Transpiler(
                MethodBase __original,
                IEnumerable<CodeInstruction> instructions)
            {
                // Find the container
                bool TryFindContainerArg(out int index)
                {
                    if (typeof(MyEntityComponentContainer).IsAssignableFrom(__original.DeclaringType))
                    {
                        index = 0;
                        return true;
                    }

                    var args = __original.GetParameters();
                    for (var i = 0; i < args.Length; i++)
                    {
                        if (typeof(MyEntityComponentContainer).IsAssignableFrom(args[i].ParameterType))
                        {
                            index = i + (__original.IsStatic ? 0 : 1);
                            return true;
                        }
                    }

                    index = default;
                    return false;
                }

                if (!TryFindContainerArg(out var containerArg))
                {
                    Entrypoint.LoggerFor(typeof(EntityComponentErrors))
                        .ZLogInformation("Failed to find container arg in {0}", __original.FullDescription());
                    foreach (var instruction in instructions)
                        yield return instruction;
                    yield break;
                }

                foreach (var instruction in instructions)
                {
                    if (instruction.operand is MethodInfo method &&
                        AliasedMethods.TryGetValue(method, out var alias))
                    {
                        Entrypoint.LoggerFor(typeof(EntityComponentErrors))
                            .ZLogInformation("Intercepting call to {0} from {1}",
                                method.Name, __original.FullDescription());
                        yield return new CodeInstruction(OpCodes.Ldarg, containerArg);
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = alias;
                    }

                    yield return instruction;
                }
            }
        }
    }
}