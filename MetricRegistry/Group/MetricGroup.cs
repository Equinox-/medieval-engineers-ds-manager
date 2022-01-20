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
                    _metrics.Add(name, val = new Counter(name));
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
                    _metrics.Add(name, val = new PerTickLeafAdder(name));
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
                    _metrics.Add(name, val = new Gauge(name, initialValue));
                return (Gauge) val;
            }
        }

        public Gauge Gauge(string name, Func<double?> getter)
        {
            return Gauge(name, () => getter() ?? double.NaN);
        }

        public Gauge Gauge(string name, Func<bool> getter)
        {
            return Gauge(name, () => getter() ? 1 : 0);
        }

        public Gauge Gauge(string name, Func<bool?> getter)
        {
            return Gauge(name, () =>
            {
                var val = getter();
                return val.HasValue ? (val.Value ? 1 : 0) : double.NaN;
            });
        }

        public Gauge Gauge(string name, Func<double> getter)
        {
            if (TryGetLock1(name, out var val))
                return (Gauge) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out val))
                    _metrics.Add(name, val = new Gauge(name, getter));
                return (Gauge) val;
            }
        }

        public Gauge SetGauge(string name, Func<double> getter)
        {
            var gauge = Gauge(name, getter);
            gauge.SetValue(getter);
            return gauge;
        }

        public override void WriteTo(MetricWriter writer)
        {
            using (_lock.AcquireSharedUsing())
                writer.WriteGroup(in _nameUnsafe, new MetricGroupReader(_metrics.GetEnumerator()));
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