using System;
using System.Text;
using System.Threading;
using InfluxDB.Client.Api.Domain;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Watchdog.Database
{
    public static class MetricSink
    {
        private static readonly ThreadLocal<StringBuilder> StringBuilderPool = new ThreadLocal<StringBuilder>(() => new StringBuilder());

        public static void Register(Program pgm)
        {
            pgm.Distributor.RegisterPacketHandler(msg => Consume(pgm.Influx, msg), Message.MetricMessage);
        }

        private static void Consume(Influx influx, PacketDistributor.MessageToken msg)
        {
            var metrics = msg.Value<MetricMessage>();
            using (var writer = influx.Write(metrics.Prefix))
            {
                // ReSharper disable PossibleInvalidOperationException
                switch (metrics.DataType)
                {
                    case MetricGroupData.NONE:
                        return;
                    case MetricGroupData.HistogramMetricData:
                        WriteHistogram(writer, metrics.Data<HistogramMetricData>().Value);
                        break;
                    case MetricGroupData.CompositeMetricData:
                        WriteComposite(writer, metrics.Data<CompositeMetricData>().Value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                // ReSharper restore PossibleInvalidOperationException

                if (!writer.HasValues)
                    return;
                
                for (var i = 0; i < metrics.TagKeyValLength; i += 2)
                    writer.WriteTag(metrics.TagKeyVal(i), metrics.TagKeyVal(i + 1), false);

                writer.TimeMs = metrics.TimeMs;
            }
        }

        private static void WriteHistogram(StreamingInfluxWriter sb, HistogramMetricData data)
        {
            // ReSharper disable once PossibleInvalidOperationException
            sb.WriteVal("min", data.Min, true);
            sb.WriteVal("mean", data.Mean, true);
            sb.WriteVal("p50", data.P50, true);
            sb.WriteVal("p75", data.P75, true);
            sb.WriteVal("p90", data.P90, true);
            sb.WriteVal("p95", data.P95, true);
            sb.WriteVal("p98", data.P98, true);
            sb.WriteVal("p99", data.P99, true);
            sb.WriteVal("p999", data.P999, true);
            sb.WriteVal("max", data.Max, true);
            sb.WriteVal("stddev", data.StdDev, true);
            sb.WriteVal("count", data.Count, true);
        }

        private static void WriteComposite(StreamingInfluxWriter sb, CompositeMetricData data)
        {
            // ReSharper disable PossibleInvalidOperationException
            for (var i = 0; i < data.MetricsLength; i++)
                switch (data.MetricsType(i))
                {
                    case LeafMetricData.NONE:
                        break;
                    case LeafMetricData.GaugeMetricData:
                        var gaugeData = data.Metrics<GaugeMetricData>(i).Value;
                        sb.WriteVal(gaugeData.Name, gaugeData.Value, false);
                        break;
                    case LeafMetricData.CounterMetricData:
                        var counterData = data.Metrics<CounterMetricData>(i).Value;
                        sb.WriteVal(counterData.Name, counterData.Value, false);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            // ReSharper restore PossibleInvalidOperationException
        }
    }
}