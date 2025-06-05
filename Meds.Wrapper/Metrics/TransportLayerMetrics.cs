using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Wrapper.Shim;
using VRage.Collections.Concurrent;
using VRage.Game;
using VRage.Library;
using VRage.Library.Collections;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.Replication;
using VRage.Serialization;
using VRage.Steam;
using BitStreamExtensions = System.BitStreamExtensions;

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
                PatchHelper.Patch(typeof(VoxelReplicableSize));
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

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> isn)
            {
                var replicablesStreaming = AccessTools.Field(ClientDataType, "ReplicableStreaming");
                var replicablesPrep = AccessTools.Field(replicablesStreaming.FieldType, "m_preparation");

                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.LoadField(ClientDataType, "State");
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.LoadField(ClientDataType, "Replicables");
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldfld, replicablesStreaming);
                yield return CodeInstruction.LoadField(replicablesStreaming.FieldType, "m_currentlyStreamedReplicables");
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldfld, replicablesStreaming);
                yield return new CodeInstruction(OpCodes.Ldfld, replicablesPrep);
                yield return CodeInstruction.LoadField(replicablesPrep.FieldType, "m_processingSet");
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.LoadField(ClientDataType, "StateGroups");
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return CodeInstruction.LoadField(ClientDataType, "AwakeGroupsQueue");
                yield return CodeInstruction.Call(typeof(StateSync), nameof(OnSendStateSync));

                foreach (var instruction in isn)
                    yield return instruction;
            }

            public static void OnSendStateSync(
                MyReplicationLayer layer,
                MyClientStateBase state,
                MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> replicables,
                HashSet<IMyReplicable> streamingReplicables,
                HashSet<IMyReplicable> processingReplicables,
                MyConcurrentDictionary<IMyStateGroup, MyStateDataEntry> stateGroups,
                FastPriorityQueue<MyStateDataEntry> awakeGroups)
            {
                if (!PlayerMetrics.TryGetHolder(state.EndpointId.Value, out var holder)) return;
                var activeGroupCount = awakeGroups.Count;
                var groupDelay = activeGroupCount > 0 ? layer.GetSyncFrameCounter() - PriorityField.Invoke(awakeGroups.First) : 0;

                holder.ReplicableCount.SetValue(replicables.Count);
                holder.ReplicablesProcessing.SetValue(processingReplicables.Count);
                holder.ReplicablesStreaming.SetValue(streamingReplicables.Count);

                holder.StateGroupCount.SetValue(stateGroups.Count);
                holder.StateGroupsActive.SetValue(activeGroupCount);
                holder.StateGroupDelay.SetValue(groupDelay);
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

        [HarmonyPatch("Sandbox.Game.Replication.MyVoxelReplicable+SerializeData", "Serialize")]
        [AlwaysPatch]
        public static class VoxelReplicableSize
        {
            private static readonly Histogram EntitySize = MetricRegistry.Histogram(MetricName.Of(WorldDownloadFragmentBytes, "fragment", "voxelEntity"));
            private static readonly Histogram DataSize = MetricRegistry.Histogram(MetricName.Of(WorldDownloadFragmentBytes, "fragment", "voxelData"));

            private static void WriteReplace<T>(BitStream stream, ref T data, MySerializeInfo info)
            {
                var report = typeof(T) == typeof(byte[]) ? DataSize
                    : typeof(MyObjectBuilder_EntityBase).IsAssignableFrom(typeof(T)) ? EntitySize
                    : null;
                var prev = stream.BytePosition;
                MySerializer.Write(stream, ref data, info);
                report?.Record(stream.BytePosition - prev);
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var serializeMethod = typeof(MySerializer)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(method =>
                    {
                        if (method.Name != nameof(MySerializer.Write) || !method.IsGenericMethodDefinition) return false;
                        var genericParams = method.GetGenericArguments();
                        if (genericParams.Length != 1) return false;
                        var args = method.GetParameters();
                        if (args.Length != 3) return false;
                        return args[0].ParameterType == typeof(BitStream)
                               && args[1].ParameterType == genericParams[0].MakeByRefType()
                               && args[2].ParameterType == typeof(MySerializeInfo);
                    })
                    .FirstOrDefault();
                var replaceMethod = AccessTools.Method(typeof(VoxelReplicableSize), nameof(WriteReplace));
                foreach (var i in instructions)
                {
                    if (i.opcode == OpCodes.Call && i.operand is MethodInfo { IsGenericMethod: true } method &&
                        method.GetGenericMethodDefinition() == serializeMethod)
                        yield return i.ChangeInstruction(OpCodes.Call, replaceMethod.MakeGenericMethod(method.GetGenericArguments()));
                    else
                        yield return i;
                }
            }
        }
    }
}