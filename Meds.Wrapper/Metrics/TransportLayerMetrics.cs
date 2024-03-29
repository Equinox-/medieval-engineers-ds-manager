using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Wrapper.Shim;
using VRage.Game;
using VRage.Library;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.Steam;
// ReSharper disable InconsistentNaming

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
        private const string WorldDownloadPrefix = "me.network.world.";
        private const string WorldDownloadSentBytes = WorldDownloadPrefix + "sent";
        private const string WorldDownloadFragmentBytes = WorldDownloadPrefix + "fragments";

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
                PatchHelper.Patch(typeof(WorldDataSize));
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

            private static IMyReplicable FindRoot(List<IMyReplicable> replicables)
            {
                if (replicables.Count == 0)
                    return null;
                var search = replicables[0];
                while (true)
                {
                    var dep = search.GetDependency();
                    if (dep == null)
                        return search;
                    search = dep;
                }
            }

            [ThreadStatic]
            private static Dictionary<Type, string> _replicableNameCache;

            private static string ReplicableName(IMyReplicable replicable)
            {
                var type = replicable.GetType();
                if (!type.IsGenericType)
                    return type.Name;
                ref var cache = ref _replicableNameCache;
                cache ??= new Dictionary<Type, string>();
                if (cache.TryGetValue(type, out var name))
                    return name;
                name = type.Name;
                var gd = type.GetGenericTypeDefinition();
                if (gd == typeof(MyEntityReplicable<>) || gd == typeof(MyGroupReplicable<>))
                {
                    var ga = type.GenericTypeArguments;
                    name = ga[0].Name;
                }

                cache.Add(type, name);
                return name;
            }

            private static void Submit(bool isCompressed, byte[] packetData, List<IMyReplicable> replicables, long startTime)
            {
                var root = FindRoot(replicables);
                var entities = 0;
                // ReSharper disable once ForCanBeConvertedToForeach
                for (var i = 0; i < replicables.Count; i++)
                {
                    var replicable = replicables[i];
                    if (replicable is IMyEntityReplicable)
                        entities++;
                }

                var name = MetricName.Of(
                    ReplicationStreamingByteCount,
                    "compressed", isCompressed ? "true" : "false",
                    "root", root != null ? ReplicableName(root) : "unknown");
                MetricRegistry.Histogram(in name).Record(packetData.Length);
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

        [HarmonyPatch(typeof(MyObjectBuilderSerializer),
            "SerializeXML",
            typeof(Stream),
            typeof(MyObjectBuilder_Base),
            typeof(MyObjectBuilderSerializer.XmlCompression),
            typeof(Type))]
        public static class WorldDataSize
        {
            private static readonly Histogram WorldSendSize = MetricRegistry.Histogram(MetricName.Of(WorldDownloadSentBytes));
            private static readonly Histogram VoxelSize = MetricRegistry.Histogram(MetricName.Of(WorldDownloadFragmentBytes, "fragment", "voxel"));
            private static readonly Histogram SectorSize = MetricRegistry.Histogram(MetricName.Of(WorldDownloadFragmentBytes, "fragment", "sector"));

            public static void Prefix(Stream writeTo, out long __state)
            {
                __state = writeTo.Position;
            }

            public static void Postfix(Stream writeTo, MyObjectBuilder_Base objectBuilder, long __state)
            {
                if (!(objectBuilder is MyObjectBuilder_World world))
                    return;
                WorldSendSize.Record(writeTo.Position - __state);
                long voxelSize = 0;
                if (world.VoxelMaps != null)
                    foreach (var voxel in world.VoxelMaps.Dictionary)
                        voxelSize += voxel.Value.LongLength;
                VoxelSize.Record(voxelSize);
                var sectorSize = world.SerializedSector?.LongLength;
                if (sectorSize.HasValue)
                    SectorSize.Record(sectorSize.Value);
            }
        }
    }
}