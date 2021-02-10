using System.Diagnostics;
using Google.FlatBuffers;
using HdrHistogram;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public abstract class HistogramMetricBase : MetricRoot
    {
        private long _countAccumulator;
        private readonly HistogramBase _histogram;
        private readonly double _scale;

        public override int UpdateRate => 5;

        public HistogramMetricBase(MetricName name, double scale) : base(name)
        {
            _histogram = MetricRegistry.HistogramFactory.Create();
            _scale = scale;
        }

        public void Record(long stopwatchTicks)
        {
            _histogram.RecordValue(stopwatchTicks);
        }

        protected override bool WriteDataTo(FlatBufferBuilder builder, out MetricGroupData type, out int offset)
        {
            type = MetricGroupData.HistogramMetricData;
            offset = -1;
            HistogramReader reader;
            lock (this)
            {
                reader = HistogramReader.Read(_histogram);
                if (reader.SampleCount <= 0)
                    return false;
                _countAccumulator += reader.SampleCount;
                if (_countAccumulator < 0)
                    _countAccumulator -= long.MinValue;
            }

            // ReSharper disable ArgumentsStyleNamedExpression
            // ReSharper disable ArgumentsStyleOther
            offset = HistogramMetricData.CreateHistogramMetricData(
                builder: builder,
                min: reader.Min * _scale,
                mean: reader.Mean * _scale,
                p50: reader.P50 * _scale,
                p75: reader.P75 * _scale,
                p90: reader.P90 * _scale,
                p95: reader.P95 * _scale,
                p98: reader.P98 * _scale,
                p99: reader.P99 * _scale,
                p999: reader.P999 * _scale,
                max: reader.Max * _scale,
                std_dev: reader.StdDev * _scale,
                count: _countAccumulator).Value;
            // ReSharper restore ArgumentsStyleOther
            // ReSharper restore ArgumentsStyleNamedExpression
            return true;
        }
    }

    public sealed class Histogram : HistogramMetricBase
    {
        public Histogram(MetricName name, double scale = 1.0) : base(name, scale)
        {
        }
    }

    public sealed class Timer : HistogramMetricBase
    {
        private static readonly double SecondsPerTick = 1.0 / Stopwatch.Frequency;

        public Timer(MetricName name) : base(name, SecondsPerTick)
        {
        }
    }
}