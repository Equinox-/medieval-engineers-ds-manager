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

        public ArrayEnumerator<MetricRoot> GetEnumerator() => new ArrayEnumerator<MetricRoot>(this.array);

        IEnumerator<MetricRoot> IEnumerable<MetricRoot>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static implicit operator HelperMetricEnumerable(MetricRoot[] array) => new HelperMetricEnumerable(array);
    }

    public interface IHelperMetric
    {
        HelperMetricEnumerable GetOutputMetrics();

        void Flush();
    }
}