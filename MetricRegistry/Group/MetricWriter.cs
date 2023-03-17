using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Meds.Metrics.Group
{
    public interface MetricWriter
    {
        void WriteGroup<T>(in MetricName name,
            T reader) where T : IEnumerator<KeyValuePair<string, LeafMetricValue>>;
        
        void WriteHistogram(
            in MetricName name,
            double min,
            double mean,
            double p50,
            double p75,
            double p90,
            double p95,
            double p98,
            double p99,
            double p999,
            double max,
            double stdDev,
            long count,
            double sum);
    }

    public enum LeafMetricType : byte
    {
        Counter,
        Gauge,
        PerTickAdder
    }

    [StructLayout(LayoutKind.Explicit)]
    public readonly struct LeafMetricValue
    {
        [FieldOffset(0)]
        private readonly long _long;

        [FieldOffset(0)]
        private readonly double _double;

        [FieldOffset(sizeof(long))]
        public readonly bool HasData;

        [FieldOffset(sizeof(long) + 1)]
        public readonly LeafMetricType Type;

        public long LongValue
        {
            get
            {
                Debug.Assert(Type == LeafMetricType.Counter);
                return _long;
            }
        }

        public double DoubleValue
        {
            get
            {
                Debug.Assert(Type == LeafMetricType.Gauge || Type == LeafMetricType.PerTickAdder);
                return _double;
            }
        }

        private LeafMetricValue(LeafMetricType type, bool hasData, long longVal)
        {
            _double = 0;
            _long = longVal;
            HasData = hasData;
            Type = type;
        }

        private LeafMetricValue(LeafMetricType type, bool hasData, double doubleVal)
        {
            _long = 0;
            _double = doubleVal;
            HasData = hasData;
            Type = type;
        }

        public static LeafMetricValue Counter(long val)
        {
            return new LeafMetricValue(LeafMetricType.Counter, true, val);
        }

        public static LeafMetricValue Gauge(double val)
        {
            return new LeafMetricValue(LeafMetricType.Gauge, !double.IsNaN(val), val);
        }

        public static LeafMetricValue PerTickAdder(double? val)
        {
            return new LeafMetricValue(LeafMetricType.PerTickAdder, val.HasValue, val ?? 0);
        }
    }
}