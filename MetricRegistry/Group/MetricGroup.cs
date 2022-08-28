using System;
using System.Collections;
using System.Collections.Generic;
using VRage.Library.Threading;

namespace Meds.Metrics.Group
{
    public sealed class MetricGroup : MetricRoot
    {
        private readonly FastResourceLock _lock = new FastResourceLock();
        private readonly Dictionary<string, LeafMetric> _metrics = new Dictionary<string, LeafMetric>();

        public MetricGroup(MetricName name) : base(name)
        {
        }

        private bool TryGetLock1(string name, out LeafMetric val)
        {
            using (_lock.AcquireSharedUsing())
                return _metrics.TryGetValue(name, out val);
        }

        public Counter Counter(string name)
        {
            if (TryGetLock1(name, out var val))
                return (Counter) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out val))
                    _metrics.Add(name, val = new Counter(this, name));
                return (Counter) val;
            }
        }

        public PerTickLeafAdder PerTickAdder(string name)
        {
            if (TryGetLock1(name, out var val))
                return (PerTickLeafAdder) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out val))
                    _metrics.Add(name, val = new PerTickLeafAdder(this, name));
                return (PerTickLeafAdder) val;
            }
        }

        public Gauge Gauge(string name, double initialValue)
        {
            if (TryGetLock1(name, out var val))
                return (Gauge) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out val))
                    _metrics.Add(name, val = new Gauge(this, name, initialValue));
                return (Gauge) val;
            }
        }

        public Gauge Gauge(string name, Func<double?> getter)
        {
            return Gauge(name, () => getter() ?? double.NaN);
        }

        public Gauge Gauge(string name, Func<bool> getter)
        {
            return Gauge(name, () => Group.Gauge.ConvertValue(getter()));
        }

        public Gauge Gauge(string name, Func<bool?> getter)
        {
            return Gauge(name, () => Group.Gauge.ConvertValue(getter()));
        }

        public Gauge Gauge(string name, Func<double> getter)
        {
            if (TryGetLock1(name, out var val))
                return (Gauge) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out val))
                    _metrics.Add(name, val = new Gauge(this, name, getter));
                return (Gauge) val;
            }
        }

        public Gauge SetGauge(string name, Func<double> getter)
        {
            var gauge = Gauge(name, getter);
            gauge.SetValue(getter);
            return gauge;
        }

        public Gauge SetGauge(string name, double value)
        {
            var gauge = Gauge(name, value);
            gauge.SetValue(value);
            return gauge;
        }

        internal void MarkChanged(bool pin = false) => LastModification = pin ? ulong.MaxValue : MetricRegistry.GcCounter;

        public override void WriteTo(MetricWriter writer)
        {
            using (_lock.AcquireSharedUsing())
                writer.WriteGroup(in NameUnsafe, new MetricGroupReader(_metrics.GetEnumerator()));
        }
    }

    public struct MetricGroupReader : IEnumerator<KeyValuePair<string, LeafMetricValue>>
    {
        private Dictionary<string, LeafMetric>.Enumerator _backing;

        public MetricGroupReader(Dictionary<string, LeafMetric>.Enumerator backing)
        {
            this._backing = backing;
        }

        public void Dispose() => _backing.Dispose();

        public bool MoveNext() => _backing.MoveNext();

        public void Reset() => ((IEnumerator) _backing).Reset();

        public KeyValuePair<string, LeafMetricValue> Current
        {
            get
            {
                var bc = _backing.Current;
                return new KeyValuePair<string, LeafMetricValue>(bc.Key, bc.Value.CurrentValue);
            }
        }

        object IEnumerator.Current => Current;
    }
}