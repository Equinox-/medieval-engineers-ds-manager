using Meds.Metrics.Group;

namespace Meds.Metrics
{
    public abstract class MetricRoot
    {
        protected MetricName _nameUnsafe;
        public MetricName Name => _nameUnsafe;

        /// <summary>
        /// Update this metric every N reporter ticks.
        /// A reporter tick is 1 minute.
        /// </summary>
        public int UpdateRate { get; set; } = 1;

        protected MetricRoot(MetricName name)
        {
            _nameUnsafe = name;
        }

        public abstract void WriteTo(MetricWriter writer);
    }
}