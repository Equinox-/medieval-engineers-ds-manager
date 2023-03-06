namespace Meds.Metrics
{
    public sealed class PerTickTimer : PerTickMetric
    {
        private readonly Histogram _perTickCount;
        private readonly Timer _perTickTime;
        private readonly Timer _perRecordTime;

        private long _count;
        private long _time;

        public PerTickTimer(in MetricName name, Timer perRecordTime, Histogram perTickCount, Timer perTickTime)
            : base(in name, new MetricRoot[] { perRecordTime, perTickCount, perTickTime })
        {
            _perTickCount = perTickCount;
            _perTickTime = perTickTime;
            _perRecordTime = perRecordTime;

            _count = 0;
            _time = 0;
        }

        public PerTickTimer(in MetricName name, Histogram perTickCount, Timer perTickTime)
            : base(in name, new MetricRoot[] { perTickCount, perTickTime })
        {
            _perTickCount = perTickCount;
            _perTickTime = perTickTime;
            _perRecordTime = null;

            _count = 0;
            _time = 0;
        }

        public int UpdateRate
        {
            set
            {
                _perTickCount.UpdateRate = value;
                _perTickTime.UpdateRate = value;
                if (_perRecordTime != null)
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
            LastModification = MetricRegistry.GcCounter;
            _perRecordTime?.Record(stopwatchTicks);
            using (StartWriting())
            {
                _count++;
                _time += stopwatchTicks;
            }
        }
    }
}