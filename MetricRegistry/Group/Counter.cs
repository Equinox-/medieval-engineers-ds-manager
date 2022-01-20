using System.Threading;

namespace Meds.Metrics.Group
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

        public override LeafMetricValue CurrentValue => LeafMetricValue.Counter(_count);
    }
}