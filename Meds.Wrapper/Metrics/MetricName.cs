using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;

namespace Meds.Wrapper.Metrics
{
    public readonly struct MetricTag : IEquatable<MetricTag>
    {
        public readonly string Key;
        public readonly string Value;

        public MetricTag(string key, string val)
        {
            Debug.Assert(string.IsNullOrEmpty(key) == string.IsNullOrEmpty(val), "Both or none of key/value must be specified");
            Key = key;
            Value = val;
        }

        [Pure]
        public bool Valid => !string.IsNullOrEmpty(Key);

        [Pure]
        public bool Equals(MetricTag other) => Key == other.Key && Value == other.Value;

        [Pure]
        public override bool Equals(object obj) => obj is MetricTag other && Equals(other);

        [Pure]
        public override int GetHashCode() => Key == null ? 0 : ((Key.GetHashCode() * 397) ^ Value.GetHashCode());
    }

    public readonly struct MetricName : IEquatable<MetricName>
    {
        public readonly string Series;
        public readonly MetricTag Kv0;
        public readonly MetricTag Kv1;
        public readonly MetricTag Kv2;
        public readonly MetricTag Kv3;

        public MetricName(string series,
            string key0 = null, string val0 = null,
            string key1 = null, string val1 = null,
            string key2 = null, string val2 = null,
            string key3 = null, string val3 = null)
        {
            Series = series;
            Kv0 = new MetricTag(key0, val0);
            Kv1 = new MetricTag(key1, val1);
            Kv2 = new MetricTag(key2, val2);
            Kv3 = new MetricTag(key3, val3);
        }

        public bool Equals(MetricName other)
        {
            return Series == other.Series && Kv0.Equals(other.Kv0) && Kv1.Equals(other.Kv1) && Kv2.Equals(other.Kv2) && Kv3.Equals(other.Kv3);
        }

        public override bool Equals(object obj)
        {
            return obj is MetricName other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Series.GetHashCode();
                hashCode = (hashCode * 397) ^ Kv0.GetHashCode();
                hashCode = (hashCode * 397) ^ Kv1.GetHashCode();
                hashCode = (hashCode * 397) ^ Kv2.GetHashCode();
                hashCode = (hashCode * 397) ^ Kv3.GetHashCode();
                return hashCode;
            }
        }

        private sealed class MetricNameEqualityComparer : IEqualityComparer<MetricName>
        {
            public bool Equals(MetricName x, MetricName y) => x.Equals(y);

            public int GetHashCode(MetricName obj) => obj.GetHashCode();
        }

        public static IEqualityComparer<MetricName> MetricNameComparer { get; } = new MetricNameEqualityComparer();
    }
}