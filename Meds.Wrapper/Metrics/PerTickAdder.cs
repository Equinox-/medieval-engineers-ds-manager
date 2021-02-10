namespace Meds.Wrapper.Metrics
{
    public sealed class PerTickAdder : PerTickMetric
    {
        private readonly Histogram _perTickCount;
        private readonly Histogram _perTickSum;
        private readonly Histogram _perRecordSum;

        private long _count;
        private long _sum;

        public PerTickAdder(Histogram perRecordSum, Histogram perTickCount, Histogram perTickSum)
        {
            _perTickCount = perTickCount;
            _perTickSum = perTickSum;
            _perRecordSum = perRecordSum;

            _count = 0;
            _sum = 0;
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
            using (StartWriting())
            {
                _perRecordSum.Record(size);
                _count++;
                _sum += size;
            }
        }
    }
}