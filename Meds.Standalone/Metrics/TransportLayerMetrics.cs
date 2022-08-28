using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Shared;
using Sandbox.Game.Multiplayer;
using VRage.Collections;
using VRage.GameServices;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Steam;
using Patches = Meds.Standalone.Shim.Patches;

namespace Meds.Standalone.Metrics
{
    public static class TransportLayerMetrics
    {
        private const string MetricPrefix = "me.network.";
        private const string ChannelGroupStats = MetricPrefix + "channel";

        public static void Register()
        {
            try
            {
                Patches.Patch(typeof(SteamPeer2PeerSend));
                Patches.Patch(typeof(SteamPeer2PeerReceive));
            }
            catch
            {
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
                PlayerMetrics.ReportNetwork(remoteUser, byteCount, 0);
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
                PlayerMetrics.ReportNetwork(remoteUser, 0, dataSize);
            }
        }
    }
}