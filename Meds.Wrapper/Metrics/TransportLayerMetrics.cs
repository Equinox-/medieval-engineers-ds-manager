using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Wrapper.Shim;
using VRage.Library;
using VRage.Network;
using VRage.Replication;
using VRage.Steam;

namespace Meds.Wrapper.Metrics
{
    public static class TransportLayerMetrics
    {
        private const string MetricPrefix = "me.network.";
        private const string ChannelGroupStats = MetricPrefix + "channel";
        private const string ReplicationStreamingPrefix = MetricPrefix + "replicable.";
        private const string ReplicationStreamingTime = ReplicationStreamingPrefix + "time";
        private const string ReplicationStreamingByteCount = ReplicationStreamingPrefix + "bytes";
        private const string ReplicationStreamingEntityCount = ReplicationStreamingPrefix + "entities";

        // for RPC sends: MyReplicationServer.DispatchEvent(BitStream stream, CallSite site, EndpointId target, IMyNetObject eventInstance, float unreliablePriority
        // for RPC rx: MyReplicationLayer.Invoke(BitStream stream, CallSite site, object obj, IMyNetObject sendAs, EndpointId source) -- return value has validation

        public static void Register()
        {
            try
            {
                PatchHelper.Patch(typeof(SteamPeer2PeerSend));
                PatchHelper.Patch(typeof(SteamPeer2PeerReceive));
                PatchHelper.Patch(typeof(StateSync));
                PatchHelper.Patch(typeof(ReplicableSerialize));
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
                if (PlayerMetrics.TryGetHolder(remoteUser, out var holder))
                    holder.BytesSent.Inc(byteCount);
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
                if (PlayerMetrics.TryGetHolder(remoteUser, out var holder))
                    holder.BytesReceived.Inc(dataSize);
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

            // private static readonly FieldInfo NetworkPing = AccessTools.Field(ClientDataType, "NetworkPing");

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
                // var ping = (MyNetworkPing)NetworkPing.GetValue(clientData);

                var groupCount = awakeGroups.Count;
                var groupDelay = groupCount > 0 ? __instance.GetSyncFrameCounter() - PriorityField.Invoke(awakeGroups.First) : 0;
                if (PlayerMetrics.TryGetHolder(state.EndpointId.Value, out var holder))
                {
                    holder.StateGroupCount.SetValue(groupCount);
                    holder.StateGroupDelay.SetValue(groupDelay);
                    // holder.Ping.Record((long)(ping.ImmediatePingMs / 1000 * Stopwatch.Frequency));
                }
            }
        }

        [HarmonyPatch("VRage.Replication.MyReplicableSerializationJob, VRage", "SerializeToBitstream")]
        public static class ReplicableSerialize
        {
            private static readonly Type PacketDesc = Type.GetType("VRage.Replication.MyReplicableSerializationPacketDesc, VRage");
            private static readonly FieldInfo IsCompressed = PacketDesc != null ? AccessTools.Field(PacketDesc, "IsCompressed") : null;
            private static readonly FieldInfo PacketData = PacketDesc != null ? AccessTools.Field(PacketDesc, "PacketData") : null;
            private static readonly FieldInfo Replicables = PacketDesc != null ? AccessTools.Field(PacketDesc, "Replicables") : null;

            private static void Submit(bool isCompressed, byte[] packetData, List<IMyReplicable> replicables, long startTime)
            {
                var name = MetricName.Of(ReplicationStreamingByteCount, "compressed", isCompressed ? "true" : "false");
                MetricRegistry.Histogram(in name).Record(packetData.Length);
                var entities = 0;
                foreach (var replicable in replicables)
                    if (replicable is IMyEntityReplicable)
                        entities++;
                MetricRegistry.Histogram(name.WithSeries(ReplicationStreamingEntityCount)).Record(entities);
                MetricRegistry.Timer(name.WithSeries(ReplicationStreamingTime)).Record(Stopwatch.GetTimestamp() - startTime);
            }

            public static bool Prepare() => IsCompressed != null && PacketData != null && Replicables != null;

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator ilg)
            {
                var startTime = ilg.DeclareLocal(typeof(long));
                yield return CodeInstruction.Call(() => Stopwatch.GetTimestamp());
                yield return new CodeInstruction(OpCodes.Stloc, startTime);
                CodeInstruction lastLoadLocalAddress = null;
                CodeInstruction jobDesc = null;

                foreach (var i in instructions)
                {
                    if (i.opcode == OpCodes.Ldloca || i.opcode == OpCodes.Ldloca_S) lastLoadLocalAddress = i;
                    if (i.operand is ConstructorInfo ctor && ctor.DeclaringType == PacketDesc && lastLoadLocalAddress != null)
                    {
                        jobDesc = new CodeInstruction(lastLoadLocalAddress.opcode, lastLoadLocalAddress.operand);
                    }

                    if (i.operand is MethodBase method
                        && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == PacketDesc
                        && jobDesc != null)
                    {
                        yield return jobDesc.Clone();
                        yield return new CodeInstruction(OpCodes.Ldfld, IsCompressed);
                        yield return jobDesc.Clone();
                        yield return new CodeInstruction(OpCodes.Ldfld, PacketData);
                        yield return jobDesc.Clone();
                        yield return new CodeInstruction(OpCodes.Ldfld, Replicables);
                        yield return new CodeInstruction(OpCodes.Ldloc, startTime);
                        yield return CodeInstruction.Call(typeof(ReplicableSerialize), nameof(Submit));
                    }

                    yield return i;
                }
            }
        }
    }
}