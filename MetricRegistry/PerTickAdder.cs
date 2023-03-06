namespace Meds.Metrics
{
    public sealed class PerTickAdder : PerTickMetric
    {
        private readonly Histogram _perTickCount;
        private readonly Histogram _perTickSum;
        private readonly Histogram _perRecordSum;

        private long _count;
        private long _sum;

        public PerTickAdder(in MetricName name, Histogram perTickCount, Histogram perTickSum)
            : base(in name, new MetricRoot[] { perTickCount, perTickSum })
        {
            _perTickCount = perTickCount;
            _perTickSum = perTickSum;
            _perRecordSum = null;

            _count = 0;
            _sum = 0;
        }

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
                if (_perRecordSum != null)
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
            _perRecordSum?.Record(size);
            using (StartWriting())
            {
                _count++;
                _sum += size;
            }
        }
    }
}