using Google.FlatBuffers;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public abstract class MetricRoot
    {
        public MetricName Name { get; }

        protected MetricRoot(MetricName name)
        {
            Name = name;
        }

        public void WriteTo(PacketBuffer buffer)
        {
            var builder = buffer.Builder;
            WriteDataTo(builder, out var dataType, out var dataOffset);

            var name = Name;
            var nameOffset = builder.CreateSharedString(name.Series);
            var keyValueTagsOffset = WriteKeyValues(builder, name);

            MetricMessage.StartMetricMessage(builder);
            MetricMessage.AddPrefix(builder, nameOffset);
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
                for (var i = 0; i < kvCount; i++)
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

        protected abstract void WriteDataTo(FlatBufferBuilder builder, out MetricGroupData type, out int offset);
    }
}