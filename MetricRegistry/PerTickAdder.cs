namespace Meds.Metrics
{
    public sealed class PerTickAdder : PerTickMetric
    {
        private readonly Histogram _perTickCount;
        private readonly Histogram _perTickSum;
        private readonly Histogram _perRecordSum;

        private long _count;
        private long _sum;

        public PerTickAdder(in MetricName name, Histogram perRecordSum, Histogram perTickCount, Histogram perTickSum)
            : base(in name, new MetricRoot[] { perRecordSum, perTickCount, perTickSum })
        {
            _perTickCount = perTickCount;
            _perTickSum = perTickSum;
            _perRecordSum = perRecordSum;

            _count = 0;
            _sum = 0;
        }

        public int UpdateRate
        {
            set
            {
                _perTickCount.UpdateRate = value;
                _perTickSum.UpdateRate = value;
                _perRecordSum.UpdateRate = value;
            }
        }

        protected override void FinishFrame()
        {
            _perTickCount.Record(_count);
            _perTickSum.Record(_sum);
            _sum = 0;
            _count = 0;
        }

        public void Record(long size)
        {
            LastModification = MetricRegistry.GcCounter;
            using (StartWriting())
            {
                _perRecordSum.Record(size);
                _count++;
                _sum += size;
            }
        }
    }
}