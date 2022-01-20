using System.Threading;

namespace Meds.Metrics.Group
{
    public sealed class PerTickLeafAdder : LeafMetric
    {
        private long _lastFlush;
        private long _sum;

        public PerTickLeafAdder(string name) : base(name)
        {
            _lastFlush = long.MinValue;
        }

        public void Add(long value)
        {
            Interlocked.CompareExchange(ref _lastFlush, long.MinValue, PerTickMetric.CurrentTick);
            Interlocked.Add(ref _sum, value);
        }

        public override LeafMetricValue CurrentValue
        {
            get
            {
                lock (this)
                {
                    var currTick = PerTickMetric.CurrentTick;
                    var prevTick = Interlocked.Exchange(ref _lastFlush, currTick);
                    var dt = currTick - prevTick;
                    if (dt == 0 || prevTick == long.MinValue)
                        return LeafMetricValue.PerTickAdder(null);

                    var sum = Interlocked.Exchange(ref _sum, 0);
                    return LeafMetricValue.PerTickAdder(sum / (double) dt);
                }
            }
        }
    }
}