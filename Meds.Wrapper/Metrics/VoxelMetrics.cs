using System;
using System.Diagnostics;
using HarmonyLib;
using Meds.Metrics;
using Meds.Wrapper.Shim;
using Sandbox.Engine.Voxels;
using VRage.Library.Utils;
using VRage.Voxels;
using VRageMath;
// ReSharper disable InconsistentNaming

namespace Meds.Wrapper.Metrics
{
    public static class VoxelMetrics
    {
        private static readonly MetricName Name = MetricName.Of("me.voxels.io");

        [AlwaysPatch]
        [HarmonyPatch(typeof(MyOctreeStorage), "WriteRangeInternal")]
        public static class VoxelWriteMetrics
        {
            public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            public static void Postfix(long __state, MyStorageDataTypeFlags dataToWrite, in Vector3I voxelRangeMin, in Vector3I voxelRangeMax)
            {
                var holder = Holders[Index(Mode.Write, dataToWrite)];
                var dt = Stopwatch.GetTimestamp() - __state;
                var volume = (voxelRangeMax - voxelRangeMin + 1).Volume();
                holder.Time.Record(dt);
                holder.Volume.Record(volume);
            }
        }


        [AlwaysPatch]
        [HarmonyPatch(typeof(MyOctreeStorage), "ReadRangeInternal")]
        public static class VoxelReadMetrics
        {
            public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

            public static void Postfix(long __state, MyStorageDataTypeFlags dataToRead, in Vector3I lodVoxelCoordStart, in Vector3I lodVoxelCoordEnd)
            {
                var holder = Holders[Index(Mode.Read, dataToRead)];
                var dt = Stopwatch.GetTimestamp() - __state;
                var volume = (lodVoxelCoordEnd - lodVoxelCoordStart + 1).Volume();
                holder.Time.Record(dt);
                holder.Volume.Record(volume);
            }
        }

        private sealed class MetricHolder
        {
            public readonly Timer Time;
            public readonly Histogram Volume;

            public MetricHolder(Mode mode, MyStorageDataTypeFlags type)
            {
                var name = Name
                    .WithTag("mode", MyEnum<Mode>.GetName(mode).ToLowerInvariant())
                    .WithTag("type", MyEnum<MyStorageDataTypeFlags>.GetName(type).ToLowerInvariant());
                Time = MetricRegistry.Timer(name.WithSuffix(".time"));
                Volume = MetricRegistry.Histogram(name.WithSuffix(".volume"));
            }
        }

        private static int Index(Mode mode, MyStorageDataTypeFlags segment) => ((int)mode << 2) | (int)segment;
        private static readonly MetricHolder[] Holders = new MetricHolder[8];

        static VoxelMetrics()
        {
            foreach (var mode in MyEnum<Mode>.Values)
            foreach (var typeRaw in new[] { 0, 1, 2, 3 })
            {
                var type = (MyStorageDataTypeFlags)typeRaw;
                ref var slot = ref Holders[Index(mode, type)];
                if (slot != null)
                    throw new ArgumentException("Slot collision");
                slot = new MetricHolder(mode, type);
            }
        }

        public enum Mode
        {
            Read,
            Write
        }
    }
}