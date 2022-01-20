using Sandbox.Game.World;
using VRage.Library.Threading;

namespace Meds.Metrics
{
    public abstract class PerTickMetric : IHelperMetric
    {
        public static int CurrentTick => MySession.Static?.GameplayFrameCounter ?? 0;
        
        private readonly FastResourceLock _lock;
        private int? _frameCounter;
        private readonly MetricRoot[] _uses;

        protected PerTickMetric(params MetricRoot[] uses)
        {
            _uses = uses;
            _lock = new FastResourceLock();
            _frameCounter = null;
        }

        protected FastResourceLockExtensions.MyExclusiveLock StartWriting()
        {
            var token = _lock.AcquireExclusiveUsing();
            CatchUpToThisFrame();
            return token;
        }

        protected abstract void FinishFrame();

        private void CatchUpToThisFrame()
        {
            var frame = CurrentTick;
            if (_frameCounter.HasValue)
            {
                var catchUp = frame - _frameCounter.Value;
                if (catchUp <= 0)
                    return;
                while (catchUp > 0)
                {
                    catchUp--;
                    FinishFrame();
                }
            }

            _frameCounter = frame;
        }

        public HelperMetricEnumerable GetOutputMetrics() => _uses;

        public void Flush()
        {
            using (StartWriting())
            {
                // do nothing -- we're caught up to this frame now
            }
        }
    }
}