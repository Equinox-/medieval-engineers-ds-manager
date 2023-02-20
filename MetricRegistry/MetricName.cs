using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;

namespace Meds.Metrics
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
        public readonly MetricTag Kv4;

        private MetricName(string series, in MetricTag kv0, in MetricTag kv1, in MetricTag kv2, in MetricTag kv3, in MetricTag kv4)
        {
            Series = series;
            Kv0 = kv0;
            Kv1 = kv1;
            Kv2 = kv2;
            Kv3 = kv3;
            Kv4 = kv4;
        }

        public static MetricName Of(string series,
            string key0 = null, string val0 = null,
            string key1 = null, string val1 = null,
            string key2 = null, string val2 = null,
            string key3 = null, string val3 = null,
            string key4 = null, string val4 = null)
        {
            return new MetricName(series,
                new MetricTag(key0, val0),
                new MetricTag(key1, val1),
                new MetricTag(key2, val2),
                new MetricTag(key3, val3),
                new MetricTag(key4, val4));
        }

        public MetricName WithSeries(string series)
        {
            return new MetricName(series, Kv0, Kv1, Kv2, Kv3, Kv4);
        }

        public MetricName WithTag(string key, string val)
        {
            if (!Kv0.Valid)
                return new MetricName(Series, new MetricTag(key, val), Kv1, Kv2, Kv3, Kv4);
            if (!Kv1.Valid)
                return new MetricName(Series, Kv0, new MetricTag(key, val), Kv2, Kv3, Kv4);
            if (!Kv2.Valid)
                return new MetricName(Series, Kv0, Kv1, new MetricTag(key, val), Kv3, Kv4);
            if (!Kv3.Valid)
                return new MetricName(Series, Kv0, Kv1, Kv2, new MetricTag(key, val), Kv4);
            if (!Kv4.Valid)
                return new MetricName(Series, Kv0, Kv1, Kv2, Kv3, new MetricTag(key, val));
            throw new Exception("Too many tags");
        }

        public MetricName WithSuffix(string suffix) => WithSeries(Series + suffix);

        public bool Equals(MetricName other)
        {
            return Series == other.Series && Kv0.Equals(other.Kv0) && Kv1.Equals(other.Kv1) && Kv2.Equals(other.Kv2) && Kv3.Equals(other.Kv3) &&
                   Kv4.Equals(other.Kv4);
        }

        public override bool Equals(object obj)
        {
            return obj is MetricName other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var tagHash = Kv0.GetHashCode() ^ Kv1.GetHashCode() ^ Kv2.GetHashCode() ^ Kv3.GetHashCode() ^ Kv4.GetHashCode();
                var seriesHash = Series.GetHashCode();
                return (seriesHash * 397) ^ tagHash;
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