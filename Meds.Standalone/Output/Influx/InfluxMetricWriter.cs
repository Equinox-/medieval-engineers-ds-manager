using System;
using System.Collections.Generic;
using Meds.Metrics;
using Meds.Metrics.Group;

namespace Meds.Standalone.Output.Influx
{
    public sealed class InfluxMetricWriter : MetricWriter
    {
        private readonly Influx _sink;

        public InfluxMetricWriter(Influx sink)
        {
            _sink = sink;
        }

        private StreamingInfluxWriter StartMetric(in MetricName name)
        {
            var writer = _sink.Write(name.Series);
            writer.TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            writer.WriteTag(name.Kv0, true);
            writer.WriteTag(name.Kv1, true);
            writer.WriteTag(name.Kv2, true);
            writer.WriteTag(name.Kv3, true);
            writer.WriteTag(name.Kv4, true);
            return writer;
        }

        public void WriteGroup<T>(in MetricName name, T reader) where T : IEnumerator<KeyValuePair<string, LeafMetricValue>>
        {
            using (var sb = StartMetric(in name))
            {
                while (reader.MoveNext())
                {
                    var entry = reader.Current;
                    if (!entry.Value.HasData) continue;
                    switch (entry.Value.Type)
                    {
                        case LeafMetricType.Counter:
                            sb.WriteVal(entry.Key, entry.Value.LongValue, true);
                            break;
                        case LeafMetricType.Gauge:
                            sb.WriteVal(entry.Key, entry.Value.DoubleValue, true);
                            break;
                        case LeafMetricType.PerTickAdder:
                            sb.WriteVal(entry.Key, entry.Value.DoubleValue, true);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        public void WriteHistogram(in MetricName name, double min, double mean, double p50, double p75, double p90, double p95, double p98, double p99,
            double p999,
            double max, double stdDev, long count)
        {
            using (var sb = StartMetric(in name))
            {
                sb.WriteVal("min", min, true);
                sb.WriteVal("mean", mean, true);
                sb.WriteVal("p50", p50, true);
                sb.WriteVal("p75", p75, true);
                sb.WriteVal("p90", p90, true);
                sb.WriteVal("p95", p95, true);
                sb.WriteVal("p98", p98, true);
                sb.WriteVal("p99", p99, true);
                sb.WriteVal("p999", p999, true);
                sb.WriteVal("max", max, true);
                sb.WriteVal("stddev", stdDev, true);
                sb.WriteVal("count", count, true);
            }
        }
    }
}