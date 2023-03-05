using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Medieval.GameSystems;
using Medieval.World.Persistence;
using Meds.Shared;
using Microsoft.Extensions.DependencyInjection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Players;
using VRage.Library.Collections;
using VRage.Logging;
using VRage.Network;
using ZLogger;
using ILogger = Microsoft.Extensions.Logging.ILogger;
// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Shim
{
    // https://communityedition.medievalengineers.com/mantis/view.php?id=317
    [HarmonyPatch]
    [AlwaysPatch(Late = true)]
    public static class PatchMtuWarning
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var type = Type.GetType("Sandbox.Engine.Multiplayer.MyTransportLayer, Sandbox.Game") ?? throw new Exception("Failed to find TransportLayer");

            var method = AccessTools.Method(type, "SendMessage", new[] { typeof(MyMessageId), typeof(BitStream), typeof(bool), typeof(EndpointId) }) ??
                         throw new Exception("Failed to find SendMessage");
            yield return method;
        }

        private const int UnreliableMaximumTransfer = 1200 - 1;
        private const int ReliableMaximumTransfer = 50 * 1024 - 1;

        public static void Prefix(MyMessageId id, BitStream stream, bool reliable)
        {
            var maxTransfer = reliable ? ReliableMaximumTransfer : UnreliableMaximumTransfer;
            if (stream != null && stream.BytePosition > maxTransfer)
                Entrypoint.LoggerFor(typeof(PatchMtuWarning))
                    .ZLogError(
                        new Exception("MTU Exceeded"),
                        "Event {0} [{1}] {2} exceeds the maximum transfer limit {3}",
                        id,
                        reliable ? "R" : "U",
                        stream.BytePosition,
                        maxTransfer);
        }
    }

    // Partial fix for https://communityedition.medievalengineers.com/mantis/view.php?id=452
    [HarmonyPatch]
    [AlwaysPatch(Late = true)]
    public static class PatchStateSyncOverflow
    {
        private static readonly MethodInfo SetBitPositionWrite = AccessTools.Method(typeof(BitStream), nameof(BitStream.SetBitPositionWrite));
        private static readonly MethodInfo GetBitPosition = AccessTools.PropertyGetter(typeof(BitStream), nameof(BitStream.BitPosition));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var type = typeof(MyReplicationServer);
            foreach (var method in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                if (method.Name == "SendStateSync" && method.GetParameters().Length == 4)
                    yield return method;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            CodeInstruction loadLocalMessageSizeFound = null;
            for (var i = list.Count - 3; i >= 0; i--)
            {
                var callBitPositionRead = list[i];
                var loadLocalMessageSize = list[i + 1];
                var branchNothingToDo = list[i + 2];
                if (callBitPositionRead.Calls(GetBitPosition) && loadLocalMessageSize.IsLdloc()
                                                              && (branchNothingToDo.opcode == OpCodes.Blt || branchNothingToDo.opcode == OpCodes.Blt_S))
                {
                    loadLocalMessageSizeFound = loadLocalMessageSize;
                    break;
                }
            }

            if (loadLocalMessageSizeFound == null)
                return list;

            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].Calls(SetBitPositionWrite)) continue;
                CodeInstruction storeLoc = null;
                var loadOp = loadLocalMessageSizeFound.opcode;
                if (loadOp == OpCodes.Ldloc_0)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_0);
                else if (loadOp == OpCodes.Ldloc_1)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_1);
                else if (loadOp == OpCodes.Ldloc_2)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_2);
                else if (loadOp == OpCodes.Ldloc_3)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_3);
                else if (loadOp == OpCodes.Ldloc_S)
                    storeLoc = new CodeInstruction(OpCodes.Stloc_S, loadLocalMessageSizeFound.operand);
                else if (loadOp == OpCodes.Ldloc)
                    storeLoc = new CodeInstruction(OpCodes.Stloc, loadLocalMessageSizeFound.operand);
                else
                    return list;

                list.Insert(i, new CodeInstruction(OpCodes.Ldc_I4, 32));
                list.Insert(i + 1, storeLoc);
                Entrypoint.LoggerFor(typeof(PatchStateSyncOverflow))
                    .ZLogInformation("Patching SendStateSync to always flush on MTU overflow.  Local={0}", storeLoc);
                return list;
            }

            return list;
        }
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=103
    [HarmonyPatch(typeof(MyBannerComponent), "OnSessionReady")]
    [AlwaysPatch]
    public static class PatchBannerLoading
    {
        public static bool Prefix() => false;
    }

    // https://communityedition.medievalengineers.com/mantis/view.php?id=459
    [HarmonyPatch(typeof(MyPersistenceViewers), "GetIdentity")]
    [AlwaysPatch]
    public static class PatchPersistenceViewerCleanup
    {
        // Save the identities of players so that we still know the identity once the player logs out.
        private static readonly Dictionary<ulong, MyIdentity> IdentityCache = new Dictionary<ulong, MyIdentity>();

        public static void Postfix(ref MyIdentity __result, ulong clientId)
        {
            if (__result != null)
                IdentityCache[clientId] = __result;
            else
                __result = IdentityCache.GetValueOrDefault(clientId, null);
        }
    }
}