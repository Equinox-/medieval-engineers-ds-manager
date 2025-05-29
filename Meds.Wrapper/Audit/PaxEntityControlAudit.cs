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
using VRage.Network;
using ZLogger;

// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Audit
{
    [HarmonyPatch]
    [AlwaysPatch(Late = true)]
    public static class PaxEntityControlAudit
    {
        private static readonly Dictionary<string, string> AddRemoveControlMethods = new Dictionary<string, string>
        {
            ["Pax.RemoteRope.MyRemoteRopeControlComponent"] = "AddRemoveControl",
            ["Pax.SteamPower.MyPAX_SteamEngineMultiplayerEvents"] = "AddRemoveControl",
        };

        private static List<MethodBase> _methods;

        public static bool Prepare()
        {
            _methods = AddRemoveControlMethods
                .SelectMany(entry => PatchHelper.ModTypes(entry.Key).SelectMany(item =>
                {
                    var method = AccessTools.Method(item.type, entry.Value);
                    if (method != null) return new[] { method };
                    Entrypoint.LoggerFor(typeof(EquiEntityControlAudit)).ZLogInformation(
                        "When patching rope controller type from {0} ({1}) the {2} method wasn't found",
                        item.mod.Id, item.mod.Name, entry.Value);
                    return Array.Empty<MethodBase>();
                })).ToList();
            return _methods.Count > 0;
        }

        public static IEnumerable<MethodBase> TargetMethods() => _methods;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __original)
        {
            IEnumerable<CodeInstruction> Inject()
            {
                var args = __original.GetParameters();
                var addIndex = -1;
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i].ParameterType == typeof(bool) && args[i].Name.Contains("add", StringComparison.OrdinalIgnoreCase)) addIndex = i;
                }

                if (addIndex == -1)
                {
                    Entrypoint.LoggerFor(typeof(EquiEntityControlAudit)).ZLogInformation(
                        "When patching rope controller method {0}, the add parameter was missing",
                        __original);
                    yield break;
                }

                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg, addIndex + 1);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PaxEntityControlAudit), nameof(AuditLog)));
            }

            foreach (var i in Inject())
                yield return i;
            foreach (var i in instructions)
                yield return i;
        }

        private static void AuditLog(MyEntityComponent controlled, bool add)
        {
            if (!add)
                return;
            var actor = MyPlayers.Static?.GetPlayer(MyEventContext.Current.Sender);
            if (actor == null)
                return;
            AuditPayload.Create(AuditEvent.PaxRopeControlStart, actor, owningLocation: controlled.Entity?.GetPosition())
                .ControlOpPayload(ControlOpPayload.Create(controlled))
                .Emit();
        }
    }
}