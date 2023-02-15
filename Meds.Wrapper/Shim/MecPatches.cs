using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using VRage.Library.Collections;
using VRage.Logging;
using VRage.Network;

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
                MyLog.Default.Error(
                    $"Event {id} {(reliable ? "[R]" : "[U]")} {stream.BytePosition} exceeds the maximum transfer limit {maxTransfer}.  It may not arrive: {new StackTrace(1)}");
        }
    }
}