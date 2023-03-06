using System;
using System.Threading;

namespace Meds.Metrics.Group
{
    public sealed class Counter : LeafMetric
    {
        private static readonly TimeSpan ReportingDelay = TimeSpan.FromMinutes(6);

        private readonly MetricGroup _group;
        private long _count;
        private DateTime? _startReportingAt;

        public Counter(MetricGroup group, string name) : base(name)
        {
            _group = group;
            _count = 0;
            _group.MarkChanged();
            _startReportingAt = null;
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
                var now = DateTime.UtcNow;
                var reportAt = _startReportingAt ??= now + ReportingDelay;
                var value = reportAt < now ? _count : 0;
                return LeafMetricValue.Counter(value);
            }
        }
    }
}