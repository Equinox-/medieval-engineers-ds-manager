using System;
using System.Diagnostics;

namespace Meds.Metrics.Group
{
    public sealed class Gauge : LeafMetric
    {
        private readonly MetricGroup _group;
        private double _value;
        private Func<double> _valueGetter;
        public override LeafMetricValue CurrentValue => LeafMetricValue.Gauge(_valueGetter?.Invoke() ?? _value);

        public static double ConvertValue(bool? value) => value.HasValue ? (value.Value ? 1 : 0) : double.NaN;

        public Gauge(MetricGroup group, string name, double initialValue) : base(name)
        {
            _group = group;
            _value = initialValue;
            _valueGetter = null;
            _group.MarkChanged();
        }

        public Gauge(MetricGroup group, string name, Func<double> getter) : base(name)
        {
            _group = group;
            _group.MarkChanged(true);
            _value = 0;
            _valueGetter = getter;
        }

        public void SetValue(Func<double> getter)
        {
            _group.MarkChanged(true);
            _valueGetter = getter;
        }

        public void SetValue(double value)
        {
            _group.MarkChanged();
            Debug.Assert(_valueGetter == null, "Set value only usable with gauges that aren't based on getters");
            _value = value;
        }
    }
}