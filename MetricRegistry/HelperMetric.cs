using System.Collections;
using System.Collections.Generic;
using VRage.Extensions;

namespace Meds.Metrics
{
    public readonly struct HelperMetricEnumerable : IEnumerable<MetricRoot>
    {
        private readonly MetricRoot[] array;

        public HelperMetricEnumerable(MetricRoot[] array)
        {
            this.array = array;
        }

        public ArrayEnumerator<MetricRoot> GetEnumerator() => new ArrayEnumerator<MetricRoot>(array);

        IEnumerator<MetricRoot> IEnumerable<MetricRoot>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static implicit operator HelperMetricEnumerable(MetricRoot[] array) => new HelperMetricEnumerable(array);
    }

    public abstract class HelperMetric
    {
        public MetricName Name { get; }

        protected HelperMetric(in MetricName name)
        {
            Name = name;
        }
        
        /// <summary>
        /// Last modification time of this metric, according to <see cref="MetricRegistry.GcCounter"/>.
        /// </summary>
        public ulong LastModification { get; protected set; }

        public abstract HelperMetricEnumerable GetOutputMetrics();

        public abstract void Flush();
    }
}