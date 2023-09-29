using System.Diagnostics;
using System.Threading;
using HdrHistogram;
using Meds.Metrics.Group;
using VRage.Library.Threading;

namespace Meds.Metrics
{
    public abstract class HistogramMetricBase : MetricRoot
    {
        public static readonly int LowestTrackableValue = 1;
        public static readonly int HighestTrackableValue = int.MaxValue / 2;
        public static readonly int NumberOfSignificantValueDigits = 3;

        private static readonly HistogramFactoryDelegate HistogramFactory = (instance, lowest, highest, significantValueDigits) =>
            new IntConcurrentHistogram(instance, lowest, highest, significantValueDigits);

        private long _countAccumulator;
        private long _sumAccumulator;
        private readonly Recorder _histogram;
        private readonly double _scale;

        protected HistogramMetricBase(MetricName name, double scale) : base(name)
        {
            _histogram = new Recorder(LowestTrackableValue, HighestTrackableValue, NumberOfSignificantValueDigits, HistogramFactory);
            _scale = scale;
            UpdateRate = 5;
        }

        public void Record(long value, int count = 1)
        {
            _histogram.RecordValueWithCount(
                value < LowestTrackableValue ? LowestTrackableValue :
                value > HighestTrackableValue ? HighestTrackableValue :
                value,
                count);
            Interlocked.Add(ref _sumAccumulator, value * count);
            LastModification = MetricRegistry.GcCounter;
        }

        private static long HandleReset(ref long value)
        {
            var val = Interlocked.Read(ref value);
            while (val < 0)
            {
                var newVal = val - long.MinValue;
                var replaced = Interlocked.CompareExchange(ref value, newVal, val);
                if (replaced == newVal)
                    break;
                val = replaced;
            }

            return val;
        }

        public override void WriteTo(MetricWriter writer)
        {
            var reader = HistogramReader.Read(_histogram);
            if (reader.SampleCount <= 0)
                return;
            Interlocked.Add(ref _countAccumulator, reader.SampleCount);
            var count = HandleReset(ref _countAccumulator);
            var sum = HandleReset(ref _sumAccumulator);

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
                count: count,
                sum: sum * _scale);
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