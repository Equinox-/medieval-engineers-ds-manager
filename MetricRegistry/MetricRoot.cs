using Meds.Metrics.Group;

namespace Meds.Metrics
{
    public abstract class MetricRoot
    {
        protected MetricName NameUnsafe;
        public MetricName Name => NameUnsafe;

        /// <summary>
        /// Last modification time of this metric, according to <see cref="MetricRegistry.GcCounter"/>.
        /// Will be <see cref="ulong.MaxValue"/> if this metric is pinned and can't be collected.
        /// </summary>
        public ulong LastModification { get; protected set; }

        /// <summary>
        /// Update this metric every N reporter ticks.
        /// A reporter tick is 1 minute.
        /// </summary>
        public int UpdateRate { get; set; } = 1;

        protected MetricRoot(MetricName name)
        {
            NameUnsafe = name;
        }

        public abstract void WriteTo(MetricWriter writer);
    }
}