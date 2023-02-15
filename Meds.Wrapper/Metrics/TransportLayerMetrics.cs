using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Meds.Wrapper.Group;
using VRage.Library;
using VRage.Network;
using VRage.Steam;
using Patches = Meds.Wrapper.Shim.Patches;

namespace Meds.Wrapper.Metrics
{
    public static class TransportLayerMetrics
    {
        private const string MetricPrefix = "me.network.";
        private const string ChannelGroupStats = MetricPrefix + "channel";

        // for RPC sends: MyReplicationServer.DispatchEvent(BitStream stream, CallSite site, EndpointId target, IMyNetObject eventInstance, float unreliablePriority
        // for RPC rx: MyReplicationLayer.Invoke(BitStream stream, CallSite site, object obj, IMyNetObject sendAs, EndpointId source) -- return value has validation

        public static void Register()
        {
            try
            {
                Patches.Patch(typeof(SteamPeer2PeerSend));
                Patches.Patch(typeof(SteamPeer2PeerReceive));
                Patches.Patch(typeof(StateSync));
            }
            catch (Exception err)
            {
                Console.WriteLine(err);
                // ignore errors
            }
        }

        private static MetricGroup ChannelMetric(int channel) => MetricRegistry.Group(MetricName.Of(ChannelGroupStats, "channel", ChannelName(channel)));

        private static string ChannelName(int channel)
        {
            switch (channel)
            {
                case MyMultiplayer.CONTROL_CHANNEL:
                    return "control";
                case MyMultiplayer.WORLD_DOWNLOAD_CHANNEL:
                    return "worldDownload";
                case MyMultiplayer.GAME_EVENT_CHANNEL:
                    return "gameEvent";
                case MyMultiplayer.VOICE_CHAT_CHANNEL:
                    return "voiceChat";
                case MyMultiplayer.BLOB_DATA_CHANNEL:
                    return "blobData";
                default:
                    return "unknown";
            }
        }

        [HarmonyPatch]
        private static class SteamPeer2PeerSend
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var method in typeof(MySteamPeer2Peer).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name != nameof(MySteamPeer2Peer.SendPacket)) continue;
                    var args = method.GetParameters().Select(x => x.Name).ToList();
                    if (!args.Contains("remoteUser")) continue;
                    if (!args.Contains("channel")) continue;
                    if (!args.Contains("dataSize") && !args.Contains("byteCount")) continue;
                    yield return method;
                }
            }

            public static void Postfix(ulong remoteUser, int byteCount, int channel)
            {
                var group = ChannelMetric(channel);
                group.Counter("messagesSent").Inc();
                group.Counter("bytesSent").Inc(byteCount);
                PlayerMetrics.ReportNetwork(remoteUser, bytesSent: byteCount);
            }
        }

        [HarmonyPatch(typeof(MySteamPeer2Peer), nameof(MySteamPeer2Peer.ReadPacket))]
        private static class SteamPeer2PeerReceive
        {
            public static void Postfix(ref ulong remoteUser, ref uint dataSize, int channel)
            {
                var group = ChannelMetric(channel);
                group.Counter("messagesReceived").Inc();
                group.Counter("bytesReceived").Inc(dataSize);
                PlayerMetrics.ReportNetwork(remoteUser, bytesReceived: dataSize);
            }
        }

        [HarmonyPatch]
        private static class StateSync
        {
            private static readonly Type ClientDataType =
                Type.GetType("VRage.Network.MyReplicationServer+ClientData, VRage") ??
                throw new NullReferenceException("Failed to resolve ClientData");

            private static readonly FieldInfo AwakeGroupsField = AccessTools.Field(ClientDataType, "AwakeGroupsQueue");

            private static readonly FieldInfo StateField = AccessTools.Field(ClientDataType, "State");

            private static readonly AccessTools.FieldRef<FastPriorityQueue<MyStateDataEntry>.Node, long> PriorityField =
                AccessTools.FieldRefAccess<FastPriorityQueue<MyStateDataEntry>.Node, long>("Priority");

            public static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var method in AccessTools.GetDeclaredMethods(typeof(MyReplicationServer)))
                {
                    if (method.Name != "SendStateSync") continue;
                    if (method.GetParameters().Length != 1) continue;
                    if (method.GetParameters()[0].ParameterType != ClientDataType) continue;
                    yield return method;
                }
            }

            public static void Postfix(MyReplicationLayer __instance, object clientData)
            {
                var awakeGroups = (FastPriorityQueue<MyStateDataEntry>)AwakeGroupsField.GetValue(clientData);
                var state = (MyClientStateBase)StateField.GetValue(clientData);

                var groupCount = awakeGroups.Count;
                var groupDelay = groupCount > 0 ? __instance.GetSyncFrameCounter() - PriorityField.Invoke(awakeGroups.First) : 0;
                PlayerMetrics.ReportNetwork(state.EndpointId.Value, stateGroupCount: groupCount, stateGroupDelay: groupDelay);
            }
        }
    }
}