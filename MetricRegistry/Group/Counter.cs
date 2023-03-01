using System.Threading;

namespace Meds.Metrics.Group
{
    public sealed class Counter : LeafMetric
    {
        private readonly MetricGroup _group;
        private long _count;
        private bool _hasBeenRead;

        public Counter(MetricGroup group, string name) : base(name)
        {
            _group = group;
            _count = 0;
            _group.MarkChanged();
            _hasBeenRead = false;
        }

        public void Inc(long n = 1)
        {
            _group.MarkChanged();
            Interlocked.Add(ref _count, n);
        }

        public override LeafMetricValue CurrentValue
        {
            get
            {
                var value = _hasBeenRead ? _count : 0;
                _hasBeenRead = true;
                return LeafMetricValue.Counter(value);
            }
        }
    }
}