namespace Meds.Metrics
{
    public sealed class PerTickTimer : PerTickMetric
    {
        private readonly Histogram _perTickCount;
        private readonly Timer _perTickTime;
        private readonly Timer _perRecordTime;

        private long _count;
        private long _time;

        public PerTickTimer(Timer perRecordTime, Histogram perTickCount, Timer perTickTime)
        {
            _perTickCount = perTickCount;
            _perTickTime = perTickTime;
            _perRecordTime = perRecordTime;

            _count = 0;
            _time = 0;
        }

        public int UpdateRate
        {
            set
            {
                _perTickCount.UpdateRate = value;
                _perTickTime.UpdateRate = value;
                _perRecordTime.UpdateRate = value;
            }
        }

        protected override void FinishFrame()
        {
            _perTickCount.Record(_count);
            _perTickTime.Record(_time);
            _time = 0;
            _count = 0;
        }

        public void Record(long stopwatchTicks)
        {
            using (StartWriting())
            {
                _perRecordTime.Record(stopwatchTicks);
                _count++;
                _time += stopwatchTicks;
            }
        }
    }
}