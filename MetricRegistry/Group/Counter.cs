using System.Threading;

namespace Meds.Metrics.Group
{
    public sealed class Counter : LeafMetric
    {
        private readonly MetricGroup _group;
        private long _count;

        public Counter(MetricGroup group, string name) : base(name)
        {
            _group = group;
            _count = 0;
            _group.MarkChanged();
        }

        public void Inc(long n = 1)
        {
            _group.MarkChanged();
            Interlocked.Add(ref _count, n);
        }

        public override LeafMetricValue CurrentValue => LeafMetricValue.Counter(_count);
    }
}