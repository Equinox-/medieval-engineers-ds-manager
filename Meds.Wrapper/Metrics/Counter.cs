using System.Threading;
using Google.FlatBuffers;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public sealed class Counter : LeafMetric
    {
        private long _count;

        public Counter(string name) : base(name)
        {
            _count = 0;
        }

        public void Inc(long n = 1)
        {
            Interlocked.Add(ref _count, n);
        }

        public override bool WriteTo(FlatBufferBuilder builder, out LeafMetricData type, out int offset)
        {
            type = LeafMetricData.CounterMetricData;
            var nameOffset = builder.CreateSharedString(Name);
            CounterMetricData.StartCounterMetricData(builder);
            CounterMetricData.AddName(builder, nameOffset);
            CounterMetricData.AddValue(builder, _count);
            offset = CounterMetricData.EndCounterMetricData(builder).Value;
            return true;
        }
    }
}