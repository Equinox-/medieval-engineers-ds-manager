using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Meds.Metrics.Group
{
    public interface MetricWriter
    {
        void WriteGroup(in MetricName name,
            MetricGroupReader reader);
        
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
            long count);
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

        private LeafMetricValue(LeafMetricType type, bool hasData, long longVal, double doubleVal)
        {
            _long = longVal;
            _double = doubleVal;
            HasData = hasData;
            Type = type;
        }

        public static LeafMetricValue Counter(long val)
        {
            return new LeafMetricValue(LeafMetricType.Counter, true, val, 0);
        }

        public static LeafMetricValue Gauge(double val)
        {
            return new LeafMetricValue(LeafMetricType.Gauge, !double.IsNaN(val), 0, val);
        }

        public static LeafMetricValue PerTickAdder(double? val)
        {
            return new LeafMetricValue(LeafMetricType.PerTickAdder, val.HasValue, 0, val ?? 0);
        }
    }
}