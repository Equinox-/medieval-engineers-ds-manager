using System;
using System.Diagnostics;
using Google.FlatBuffers;
using Meds.Shared.Data;

namespace Meds.Wrapper.Metrics
{
    public sealed class Gauge : LeafMetric
    {
        private double _value;
        private Func<double> _valueGetter;

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

        public override bool WriteTo(FlatBufferBuilder builder, out LeafMetricData type, out int offset)
        {
            type = LeafMetricData.GaugeMetricData;
            offset = -1;
            double val;
            try
            {
                val = _valueGetter?.Invoke() ?? _value;
            }
            catch
            {
                // Ignored
                return false;
            }

            if (double.IsNaN(val))
                return false;

            var nameOffset = builder.CreateSharedString(Name);
            GaugeMetricData.StartGaugeMetricData(builder);
            GaugeMetricData.AddName(builder, nameOffset);
            GaugeMetricData.AddValue(builder, val);
            offset = GaugeMetricData.EndGaugeMetricData(builder).Value;
            return true;
        }
    }
}