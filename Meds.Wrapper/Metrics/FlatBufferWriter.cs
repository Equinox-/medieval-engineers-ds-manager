using System;
using System.Collections.Generic;
using System.Threading;
using Google.FlatBuffers;
using Meds.Metrics;
using Meds.Metrics.Group;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public sealed class FlatBufferWriter : MetricWriter
    {
        private static readonly ThreadLocal<FlatBufferWriter> Pool = new ThreadLocal<FlatBufferWriter>(() => new FlatBufferWriter());
        private PacketBuffer _buffer;

        public static FlatBufferWriter Borrow(PacketBuffer dest)
        {
            var writer = Pool.Value;
            writer._buffer = dest;
            return writer;
        }

        private void WriteInternal(MetricGroupData dataType, int dataOffset, in MetricName name)
        {
            var builder = _buffer.Builder;
            var nameOffset = builder.CreateSharedString(name.Series);
            var keyValueTagsOffset = WriteKeyValues(builder, name);

            MetricMessage.StartMetricMessage(builder);
            MetricMessage.AddPrefix(builder, nameOffset);
            MetricMessage.AddTimeMs(builder, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            if (keyValueTagsOffset.HasValue)
                MetricMessage.AddTagKeyVal(builder, keyValueTagsOffset.Value);
            MetricMessage.AddDataType(builder, dataType);
            MetricMessage.AddData(builder, dataOffset);
            _buffer.EndMessage(Message.MetricMessage);
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
                WriteKv(builder, name.Kv4, kvOffsets, ref kvCount);

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

        private const int MaxMetricsInGroup = 32;

        private bool WriteLeafMetric(string name, LeafMetricValue value, out LeafMetricData type, out int offset)
        {
            type = LeafMetricData.NONE;
            offset = -1;
            if (!value.HasData)
                return false;
            switch (value.Type)
            {
                case LeafMetricType.Counter:
                    type = LeafMetricData.CounterMetricData;
                    offset = CounterMetricData.CreateCounterMetricData(_buffer.Builder,
                        _buffer.Builder.CreateSharedString(name),
                        value.LongValue).Value;
                    return true;
                case LeafMetricType.Gauge:
                    type = LeafMetricData.GaugeMetricData;
                    offset = GaugeMetricData.CreateGaugeMetricData(_buffer.Builder,
                        _buffer.Builder.CreateSharedString(name),
                        value.DoubleValue).Value;
                    return true;
                case LeafMetricType.PerTickAdder:
                    type = LeafMetricData.GaugeMetricData;
                    offset = GaugeMetricData.CreateGaugeMetricData(_buffer.Builder,
                        _buffer.Builder.CreateSharedString(name),
                        value.DoubleValue).Value;
                    return true;
                default:
                    return false;
            }
        }

        public void WriteGroup<T>(in MetricName name, T reader) where T : IEnumerator<KeyValuePair<string, LeafMetricValue>>
        {
            unsafe
            {
                var childOffsets = stackalloc int[MaxMetricsInGroup];
                var childTypes = stackalloc LeafMetricData[MaxMetricsInGroup];
                var offsetCount = 0;
                while (reader.MoveNext())
                {
                    var curr = reader.Current;
                    if (WriteLeafMetric(curr.Key, curr.Value, out childTypes[offsetCount], out childOffsets[offsetCount]))
                        offsetCount++;
                }

                if (offsetCount == 0)
                    return;
                var builder = _buffer.Builder;

                CompositeMetricData.StartMetricsTypeVector(builder, offsetCount);
                for (var i = 0; i < offsetCount; i++)
                    builder.AddByte((byte)childTypes[i]);
                var typeVector = builder.EndVector();
                CompositeMetricData.StartMetricsVector(builder, offsetCount);
                for (var i = 0; i < offsetCount; i++)
                    builder.AddOffset(childOffsets[i]);
                var offsetVector = builder.EndVector();

                CompositeMetricData.StartCompositeMetricData(builder);
                CompositeMetricData.AddMetrics(builder, offsetVector);
                CompositeMetricData.AddMetricsType(builder, typeVector);
                var offset = CompositeMetricData.EndCompositeMetricData(builder).Value;

                WriteInternal(MetricGroupData.CompositeMetricData, offset, in name);
            }
        }

        public void WriteHistogram(in MetricName name, double min, double mean, double p50, double p75, double p90, double p95, double p98, double p99,
            double p999, double max,
            double stdDev, long count)
        {
            var offset = HistogramMetricData.CreateHistogramMetricData(_buffer.Builder,
                min, mean, p50, p75, p90, p95, p98, p99, p999,
                max, stdDev, count);
            WriteInternal(MetricGroupData.HistogramMetricData, offset.Value, in name);
        }
    }
}