using System;
using System.Diagnostics;

namespace Meds.Metrics.Group
{
    public sealed class Gauge : LeafMetric
    {
        private double _value;
        private Func<double> _valueGetter;
        public override LeafMetricValue CurrentValue => LeafMetricValue.Gauge(_valueGetter?.Invoke() ?? _value);

        public Gauge(string name, double initialValue) : base(name)
        {
            _value = initialValue;
            _valueGetter = null;
        }

        public Gauge(string name, Func<double> getter) : base(name)
        {
            _value = 0;
            _valueGetter = getter;
        }

        public void SetValue(Func<double> getter)
        {
            _valueGetter = getter;
        }

        public void SetValue(double value)
        {
            Debug.Assert(_valueGetter == null, "Set value only usable with gauges that aren't based on getters");
            _value = value;
        }
    }
}