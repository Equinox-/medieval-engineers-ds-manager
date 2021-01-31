using System.Diagnostics;
using Google.FlatBuffers;
using HdrHistogram;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public sealed class MetricTimer : MetricRoot
    {
        private static readonly double NanosPerTick = 1e9 / Stopwatch.Frequency;
        private readonly HistogramBase _histogram;

        public MetricTimer(MetricName name) : base(name)
        {
            _histogram = MetricRegistry.TimerFactory.Create();
        }

        private static long ToNanos(long ticks)
        {
            return (long) (ticks * NanosPerTick);
        }

        public void Record(long stopwatchTicks)
        {
            _histogram.RecordValue(stopwatchTicks);
        }

        protected override void WriteDataTo(FlatBufferBuilder builder, out MetricGroupData type, out int offset)
        {
            _histogram.GetStdDeviation()
            _histogram.GetValueAtPercentile()
            throw new System.NotImplementedException();
        }
    }
}