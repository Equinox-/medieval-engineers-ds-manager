using System.Diagnostics;
using HdrHistogram;
using Meds.Metrics.Group;

namespace Meds.Metrics
{
    public abstract class HistogramMetricBase : MetricRoot
    {
        public static readonly int LowestTrackableValue = 1;
        public static readonly int HighestTrackableValue = int.MaxValue / 2;
        public static readonly int NumberOfSignificantValueDigits = 3;

        private long _countAccumulator;
        private readonly HistogramBase _histogram;
        private readonly double _scale;

        public HistogramMetricBase(MetricName name, double scale) : base(name)
        {
            _histogram = new IntConcurrentHistogram(LowestTrackableValue, HighestTrackableValue, NumberOfSignificantValueDigits);
            _scale = scale;
            UpdateRate = 5;
        }

        public void Record(long stopwatchTicks)
        {
            _histogram.RecordValue(stopwatchTicks);
            LastModification = MetricRegistry.GcCounter;
        }

        public override void WriteTo(MetricWriter writer)
        {
            HistogramReader reader;
            lock (this)
            {
                reader = HistogramReader.Read(_histogram);
                if (reader.SampleCount <= 0)
                    return;
                _countAccumulator += reader.SampleCount;
                if (_countAccumulator < 0)
                    _countAccumulator -= long.MinValue;
            }

            // ReSharper disable ArgumentsStyleNamedExpression
            // ReSharper disable ArgumentsStyleOther
            writer.WriteHistogram(in NameUnsafe,
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
                stdDev: reader.StdDev * _scale,
                count: _countAccumulator);
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