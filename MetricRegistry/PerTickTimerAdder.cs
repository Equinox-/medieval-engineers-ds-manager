namespace Meds.Metrics
{
    public sealed class PerTickTimerAdder : PerTickMetric
    {
        private readonly Histogram _perTickCount;
        private readonly Timer _perTickTime;
        private readonly Histogram _perTickSum;
        private readonly Timer _perRecordTime;
        private readonly Histogram _perRecordSum;

        private long _count;
        private long _time;
        private long _sum;

        public PerTickTimerAdder(Timer perRecordTime,
            Histogram perRecordSum,
            Histogram perTickCount,
            Timer perTickTime,
            Histogram perTickSum)
        {
            _perRecordTime = perRecordTime;
            _perRecordSum = perRecordSum;

            _perTickCount = perTickCount;
            _perTickTime = perTickTime;
            _perTickSum = perTickSum;

            _count = 0;
            _time = 0;
            _sum = 0;
        }

        public int UpdateRate
        {
            set
            {
                _perTickCount.UpdateRate = value;
                _perTickTime.UpdateRate = value;
                _perTickSum.UpdateRate = value;
                _perRecordSum.UpdateRate = value;
                _perRecordTime.UpdateRate = value;
            }
        }

        protected override void FinishFrame()
        {
            _perTickCount.Record(_count);
            _perTickTime.Record(_time);
            _perTickSum.Record(_sum);
            _time = 0;
            _count = 0;
            _sum = 0;
        }

        public void Record(long stopwatchTicks, long size)
        {
            using (StartWriting())
            {
                _perRecordSum.Record(size);
                _perRecordTime.Record(stopwatchTicks);
                _count++;
                _sum += size;
                _time += stopwatchTicks;
            }
        }
    }
}