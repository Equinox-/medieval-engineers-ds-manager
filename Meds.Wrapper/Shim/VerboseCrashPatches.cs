using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Wrapper.Utils;
using Microsoft.Extensions.Logging;
using Sandbox.Game.Entities.Entity.Stats;
using Sandbox.Game.EntityComponents.Character;
using VRage.Components;
using VRage.Game.Components;
using ZLogger;

namespace Meds.Wrapper.Shim
{
    [HarmonyPatch(typeof(MyUpdateScheduler), "ReportError")]
    [AlwaysPatch]
    public static class VerboseUpdateSchedulerCrash
    {
        private readonly struct LoggingPayloadConsumer : IPayloadConsumer
        {
            private readonly Delegate _action;
            private readonly Exception _error;

            public LoggingPayloadConsumer(Delegate action, Exception error)
            {
                _action = action;
                _error = error;
            }

            public void Consume<T>(in T payload)
            {
                Entrypoint
                    .LoggerFor(_action.Method.DeclaringType ?? typeof(VerboseUpdateSchedulerCrash))
                    .ZLogErrorWithPayload(_error, payload, "Update method failed: {0} on {1}",
                        _action.Method.FullDescription(), _action.Target ?? "null");
            }
        }

        public static void Prefix(Delegate action, Exception error) => LoggingPayloads.VisitPayload(action, new LoggingPayloadConsumer(action, error));
    }


    [HarmonyPatch]
    [AlwaysPatch]
    public static class VerboseEntityComponentError
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
            x => AccessTools.Method(typeof(VerboseEntityComponentError), x));

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
                Entrypoint.LoggerFor(typeof(VerboseEntityComponentError))
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
                    Entrypoint.LoggerFor(typeof(VerboseEntityComponentError))
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