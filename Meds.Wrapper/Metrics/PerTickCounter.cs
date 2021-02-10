namespace Meds.Wrapper.Metrics
{
    public sealed class PerTickCounter : PerTickMetric
    {
        private readonly Histogram _perTickCount;

        private long _count;

        public PerTickCounter(Histogram perTickCount)
        {
            _perTickCount = perTickCount;

            _count = 0;
        }

        protected override void FinishFrame()
        {
            _perTickCount.Record(_count);
            _count = 0;
        }

        public void Record(long count)
        {
            using (StartWriting())
            {
                _count += count;
            }
        }
    }
}