using System;
using Google.FlatBuffers;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public abstract class MetricRoot
    {
        public MetricName Name { get; }

        /// <summary>
        /// Update this metric every N reporter ticks.
        /// </summary>
        public virtual int UpdateRate => 1;

        protected MetricRoot(MetricName name)
        {
            Name = name;
        }

        public void WriteTo(PacketBuffer buffer)
        {
            var builder = buffer.Builder;
            if (!WriteDataTo(builder, out var dataType, out var dataOffset))
                return;

            var name = Name;
            var nameOffset = builder.CreateSharedString(name.Series);
            var keyValueTagsOffset = WriteKeyValues(builder, name);

            MetricMessage.StartMetricMessage(builder);
            MetricMessage.AddPrefix(builder, nameOffset);
            MetricMessage.AddTimeMs(builder, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            if (keyValueTagsOffset.HasValue)
                MetricMessage.AddTagKeyVal(builder, keyValueTagsOffset.Value);
            MetricMessage.AddDataType(builder, dataType);
            MetricMessage.AddData(builder, dataOffset);
            buffer.EndMessage(Message.MetricMessage);
        }

        private static VectorOffset? WriteKeyValues(FlatBufferBuilder builder, MetricName name)
        {
            unsafe
            {
                // Write KV tags
                var kvOffsets = stackalloc StringOffset[4 * 2];
                var kvCount = 0;

                WriteKv(builder, name.Kv0, kvOffsets, ref kvCount);
                WriteKv(builder, name.Kv1, kvOffsets, ref kvCount);
                WriteKv(builder, name.Kv2, kvOffsets, ref kvCount);
                WriteKv(builder, name.Kv3, kvOffsets, ref kvCount);

                if (kvCount <= 0) return null;
                MetricMessage.StartTagKeyValVector(builder, kvCount);
                for (var i = kvCount - 1; i >= 0; i--)
                    builder.AddOffset(kvOffsets[i].Value);
                return builder.EndVector();
            }
        }

        private static unsafe void WriteKv(FlatBufferBuilder builder, MetricTag tag, StringOffset* offsets, ref int count)
        {
            if (!tag.Valid)
                return;
            offsets[count++] = builder.CreateSharedString(tag.Key);
            offsets[count++] = builder.CreateSharedString(tag.Value);
        }

        protected abstract bool WriteDataTo(FlatBufferBuilder builder, out MetricGroupData type, out int offset);
    }
}