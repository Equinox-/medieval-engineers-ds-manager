using System;
using System.Collections.Generic;
using Google.FlatBuffers;
using Meds.Shared.Data;
using VRage.Library.Threading;

namespace Meds.Wrapper.Metrics
{
    public sealed class MetricGroup : MetricRoot
    {
        private const int MaxMetricsInGroup = 32;

        private readonly FastResourceLock _lock = new FastResourceLock();
        private readonly Dictionary<string, LeafMetric> _metrics = new Dictionary<string, LeafMetric>();

        public MetricGroup(MetricName name) : base(name)
        {
        }

        public Counter Counter(string name)
        {
            using (_lock.AcquireSharedUsing())
                if (_metrics.TryGetValue(name, out var val))
                    return (Counter) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out var val))
                    _metrics.Add(name, val = new Counter(name));
                return (Counter) val;
            }
        }

        public Gauge Gauge(string name, double initialValue)
        {
            using (_lock.AcquireSharedUsing())
                if (_metrics.TryGetValue(name, out var val))
                    return (Gauge) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out var val))
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
            using (_lock.AcquireSharedUsing())
                if (_metrics.TryGetValue(name, out var val))
                    return (Gauge) val;
            using (_lock.AcquireExclusiveUsing())
            {
                if (!_metrics.TryGetValue(name, out var val))
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

        protected override bool WriteDataTo(FlatBufferBuilder builder, out MetricGroupData type, out int offset)
        {
            type = MetricGroupData.CompositeMetricData;
            offset = -1;
            unsafe
            {
                var childOffsets = stackalloc int[MaxMetricsInGroup];
                var childTypes = stackalloc LeafMetricData[MaxMetricsInGroup];
                var offsetCount = 0;
                using (_lock.AcquireSharedUsing())
                    foreach (var metric in _metrics.Values)
                        if (metric.WriteTo(builder, out childTypes[offsetCount], out childOffsets[offsetCount]))
                            offsetCount++;

                if (offsetCount == 0)
                    return false;

                CompositeMetricData.StartMetricsTypeVector(builder, offsetCount);
                for (var i = 0; i < offsetCount; i++)
                    builder.AddByte((byte) childTypes[i]);
                var typeVector = builder.EndVector();
                CompositeMetricData.StartMetricsVector(builder, offsetCount);
                for (var i = 0; i < offsetCount; i++)
                    builder.AddOffset(childOffsets[i]);
                var offsetVector = builder.EndVector();

                CompositeMetricData.StartCompositeMetricData(builder);
                CompositeMetricData.AddMetrics(builder, offsetVector);
                CompositeMetricData.AddMetricsType(builder, typeVector);
                offset = CompositeMetricData.EndCompositeMetricData(builder).Value;
            }

            return true;
        }
    }
}