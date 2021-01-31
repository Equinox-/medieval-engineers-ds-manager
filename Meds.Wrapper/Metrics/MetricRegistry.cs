using System.Collections.Generic;
using HdrHistogram;
using VRage.Collections.Concurrent;
using VRage.Library.Threading;

namespace Meds.Wrapper.Metrics
{
    public static class MetricRegistry
    {
        private static readonly FastResourceLock _lock = new FastResourceLock();
        private static readonly List<MetricRoot> _metricRoots = new List<MetricRoot>();
        private static readonly Dictionary<MetricName, MetricRoot> _metricsByName = new Dictionary<MetricName, MetricRoot>();

        /**
         * Histogram for stopwatch tick precision timers.
         */
        public static readonly HistogramFactory TimerFactory = HistogramFactory.With64BitBucketSize()
            .WithThreadSafeWrites();
    }
}