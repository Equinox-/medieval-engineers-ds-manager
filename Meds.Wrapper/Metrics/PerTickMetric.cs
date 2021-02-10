using Sandbox.Game.World;
using VRage.Library.Threading;

namespace Meds.Wrapper.Metrics
{
    public abstract class PerTickMetric : IHelperMetric
    {
        private readonly FastResourceLock _lock;
        private int? _frameCounter;

        protected PerTickMetric()
        {
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
            var frame = MySession.Static?.GameplayFrameCounter ?? 0;
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

        public void Flush()
        {
            using (StartWriting())
            {
                // do nothing -- we're caught up to this frame now
            }
        }
    }
}