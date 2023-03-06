using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using VRage;

namespace Meds.Metrics
{
    public readonly struct MetricTag : IEquatable<MetricTag>
    {
        public readonly string Key;
        public readonly string Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MetricTag(string key, string val)
        {
            Debug.Assert(string.IsNullOrEmpty(key) == string.IsNullOrEmpty(val), "Both or none of key/value must be specified");
            Key = key;
            Value = val;
        }

        [Pure]
        public bool Valid => !string.IsNullOrEmpty(Key);

        [Pure]
        public bool Equals(in MetricTag other) => Key == other.Key && Value == other.Value;

        [Pure]
        bool IEquatable<MetricTag>.Equals(MetricTag other) => Equals(in other);

        [Pure]
        public override bool Equals(object obj) => obj is MetricTag other && Equals(other);

        [Pure]
        public override int GetHashCode() => Key == null ? 0 : (Key.GetHashCode() * 397) ^ Value.GetHashCode();

        [Pure]
        public bool ReferenceEquals(in MetricTag other) => ReferenceEquals(Key, other.Key) && ReferenceEquals(Value, other.Value);

        [Pure]
        public int GetReferenceHashCode() => (RuntimeHelpers.GetHashCode(Key) * 397) ^ RuntimeHelpers.GetHashCode(Value);
    }

    public readonly struct MetricName : IEquatable<MetricName>
    {
        public readonly string Series;
        public readonly MetricTag Kv0;
        public readonly MetricTag Kv1;
        public readonly MetricTag Kv2;
        public readonly MetricTag Kv3;
        public readonly MetricTag Kv4;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MetricName(string series)
        {
            Series = series;
            Kv0 = default;
            Kv1 = default;
            Kv2 = default;
            Kv3 = default;
            Kv4 = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MetricName(string series, in MetricTag kv0)
        {
            Series = series;
            Kv0 = kv0;
            Kv1 = default;
            Kv2 = default;
            Kv3 = default;
            Kv4 = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MetricName(string series, in MetricTag kv0, in MetricTag kv1)
        {
            Series = series;
            Kv0 = kv0;
            Kv1 = kv1;
            Kv2 = default;
            Kv3 = default;
            Kv4 = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MetricName(string series, in MetricTag kv0, in MetricTag kv1, in MetricTag kv2)
        {
            Series = series;
            Kv0 = kv0;
            Kv1 = kv1;
            Kv2 = kv2;
            Kv3 = default;
            Kv4 = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MetricName(string series, in MetricTag kv0, in MetricTag kv1, in MetricTag kv2, in MetricTag kv3)
        {
            Series = series;
            Kv0 = kv0;
            Kv1 = kv1;
            Kv2 = kv2;
            Kv3 = kv3;
            Kv4 = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MetricName(string series, in MetricTag kv0, in MetricTag kv1, in MetricTag kv2, in MetricTag kv3, in MetricTag kv4)
        {
            Series = series;
            Kv0 = kv0;
            Kv1 = kv1;
            Kv2 = kv2;
            Kv3 = kv3;
            Kv4 = kv4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MetricName Of(string series)
        {
            return new MetricName(series);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MetricName Of(string series,
            string key0, string val0)
        {
            return new MetricName(series,
                new MetricTag(key0, val0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MetricName Of(string series,
            string key0, string val0,
            string key1, string val1)
        {
            return new MetricName(series,
                new MetricTag(key0, val0),
                new MetricTag(key1, val1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MetricName Of(string series,
            string key0, string val0,
            string key1, string val1,
            string key2, string val2)
        {
            return new MetricName(series,
                new MetricTag(key0, val0),
                new MetricTag(key1, val1),
                new MetricTag(key2, val2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MetricName Of(string series,
            string key0, string val0,
            string key1, string val1,
            string key2, string val2,
            string key3, string val3)
        {
            return new MetricName(series,
                new MetricTag(key0, val0),
                new MetricTag(key1, val1),
                new MetricTag(key2, val2),
                new MetricTag(key3, val3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MetricName Of(string series,
            string key0, string val0,
            string key1, string val1,
            string key2, string val2,
            string key3, string val3,
            string key4, string val4)
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
            return new MetricName(series, in Kv0, in Kv1, in Kv2, in Kv3, in Kv4);
        }

        public MetricName WithTag(string key, string val)
        {
            if (!Kv0.Valid)
                return new MetricName(Series, new MetricTag(key, val), in Kv1, in Kv2, in Kv3, in Kv4);
            if (!Kv1.Valid)
                return new MetricName(Series, in Kv0, new MetricTag(key, val), in Kv2, in Kv3, in Kv4);
            if (!Kv2.Valid)
                return new MetricName(Series, in Kv0, in Kv1, new MetricTag(key, val), in Kv3, in Kv4);
            if (!Kv3.Valid)
                return new MetricName(Series, in Kv0, in Kv1, in Kv2, new MetricTag(key, val), in Kv4);
            if (!Kv4.Valid)
                return new MetricName(Series, in Kv0, in Kv1, in Kv2, in Kv3, new MetricTag(key, val));
            throw new Exception("Too many tags");
        }

        public MetricName WithSuffix(string suffix) => WithSeries(Series + suffix);

        [Pure]
        public bool Equals(in MetricName other)
        {
            return Series == other.Series
                   && Kv0.Equals(in other.Kv0)
                   && Kv1.Equals(in other.Kv1)
                   && Kv2.Equals(in other.Kv2)
                   && Kv3.Equals(in other.Kv3)
                   && Kv4.Equals(in other.Kv4);
        }

        [Pure]
        bool IEquatable<MetricName>.Equals(MetricName other) => Equals(in other);

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

        [Pure]
        public bool ReferenceEquals(in MetricName other)
        {
            return ReferenceEquals(Series, other.Series)
                   && Kv0.ReferenceEquals(in other.Kv0)
                   && Kv1.ReferenceEquals(in other.Kv1)
                   && Kv2.ReferenceEquals(in other.Kv2)
                   && Kv3.ReferenceEquals(in other.Kv3)
                   && Kv4.ReferenceEquals(in other.Kv4);
        }

        [Pure]
        public int GetReferenceHashCode()
        {
            unchecked
            {
                var tagHash = Kv0.GetReferenceHashCode()
                              ^ Kv1.GetReferenceHashCode()
                              ^ Kv2.GetReferenceHashCode()
                              ^ Kv3.GetReferenceHashCode()
                              ^ Kv4.GetReferenceHashCode();
                var seriesHash = RuntimeHelpers.GetHashCode(Series);
                return (seriesHash * 397) ^ tagHash;
            }
        }

        private sealed class MetricNameEqualityComparer : IEqualityComparer<MetricName>
        {
            public bool Equals(MetricName x, MetricName y) => x.Equals(y);

            public int GetHashCode(MetricName obj) => obj.GetHashCode();
        }

        public static IEqualityComparer<MetricName> MetricNameComparer { get; } = new MetricNameEqualityComparer();

        private sealed class ReferenceEqualityComparer : IEqualityComparer<MetricName>
        {

            public bool Equals(MetricName x, MetricName y) => x.ReferenceEquals(y);

            public int GetHashCode(MetricName obj) => obj.GetReferenceHashCode();
        }

        public static IEqualityComparer<MetricName> ReferenceComparer { get; } = new ReferenceEqualityComparer();
    }
}