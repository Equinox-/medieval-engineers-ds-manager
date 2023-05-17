using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Wrapper.Shim;
using Sandbox.Game.Players;
using VRage.Game;
using VRage.Game.Components;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    [HarmonyPatch]
    [AlwaysPatch(Late = true)]
    public static class EquiEntityControlAudit
    {
        private const string ControllerType = "Equinox76561198048419394.Core.Controller.EquiEntityControllerComponent";

        private static List<MethodBase> _methods;

        public static bool Prepare()
        {
            _methods = PatchHelper.ModTypes(ControllerType).SelectMany(item =>
            {
                var method = AccessTools.Method(item.type, "RequestControlInternal");
                if (method != null) return new[] { method };
                Entrypoint.LoggerFor(typeof(EquiEntityControlAudit)).ZLogInformation(
                    "When patching entity controller type from {0} ({1}) the RequestControlInternal method wasn't found",
                    item.mod.Id, item.mod.Name);
                return Array.Empty<MethodBase>();
            }).ToList();
            return _methods.Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods() => _methods;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __original)
        {
            IEnumerable<CodeInstruction> Inject()
            {
                var args = __original.GetParameters();
                if (args.Length < 1)
                {
                    yield break;
                }

                var slotType = args[0].ParameterType;
                var slotDefinition = slotType.GetField("Definition");
                var slotDefinitionName = slotDefinition?.FieldType.GetProperty("Name")?.GetMethod;
                var slotControllable = slotType.GetField("Controllable");
                var slotControllableDefinition =
                    slotControllable?.FieldType.GetField("_definition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (slotDefinition == null || slotControllable == null || slotDefinitionName == null || slotControllableDefinition == null)
                {
                    Entrypoint.LoggerFor(typeof(EquiEntityControlAudit)).ZLogInformation(
                        "When patching entity controller method {0}, on the slot type {1}, with slot definition {2}, slot holder {3}, fields were missing",
                        __original, slotType, slotDefinition?.FieldType, slotControllable?.FieldType);
                    yield break;
                }

                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldfld, slotControllable);
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldfld, slotControllableDefinition);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldfld, slotDefinition);
                yield return new CodeInstruction(OpCodes.Callvirt, slotDefinitionName);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EquiEntityControlAudit), nameof(AuditLog)));
            }

            foreach (var i in Inject())
                yield return i;
            foreach (var i in instructions)
                yield return i;
        }

        private static void AuditLog(MyEntityComponent controller, MyEntityComponent controlled,
            MyEntityComponentDefinition controlledDefinition, string slotName)
        {
            var actor = MyPlayers.Static?.GetControllingPlayer(controller.Entity);
            if (actor == null)
                return;
            AuditPayload.Create(AuditEvent.EquiControlStart, actor, owningLocation: controlled.Entity?.GetPosition())
                .ControlOpPayload(new ControlOpPayload
                {
                    Entity = controlled.Entity?.DefinitionId?.SubtypeName,
                    Component = controlledDefinition?.Id.SubtypeName,
                    Slot = slotName,
                })
                .Emit();
        }
    }
}