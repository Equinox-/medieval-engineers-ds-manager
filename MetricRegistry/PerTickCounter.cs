namespace Meds.Metrics
{
    public sealed class PerTickCounter : PerTickMetric
    {
        private readonly Histogram _perTickCount;

        private long _count;

        public PerTickCounter(in MetricName name, Histogram perTickCount) : base(in name, new MetricRoot[] { perTickCount })
        {
            _perTickCount = perTickCount;

            _count = 0;
        }

        public int UpdateRate
        {
            set => _perTickCount.UpdateRate = value;
        }

        protected override void FinishFrame()
        {
            _perTickCount.Record(_count);
            _count = 0;
        }

        public void Record(long count)
        {
            LastModification = MetricRegistry.GcCounter;
            using (StartWriting())
            {
                _count += count;
            }
        }
    }
}