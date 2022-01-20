using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Meds.Metrics;
using VRage.Library.Extensions;

namespace Meds.Standalone.Output
{
    public sealed class StreamingInfluxWriter : IDisposable
    {
        private readonly Action<StringBuilder> _consumer;
        private readonly string _defaultTags;
        public StringBuilder Tags { get; }
        public StringBuilder Values { get; }
        public long TimeMs { get; set; }

        public bool HasValues => Values.Length > 0;

        public StreamingInfluxWriter(string defaultTag, Action<StringBuilder> consumer)
        {
            _defaultTags = defaultTag;
            _consumer = consumer;
            Tags = new StringBuilder();
            Values = new StringBuilder();
        }

        public StreamingInfluxWriter Reset(string measurement)
        {
            Tags.Clear();
            Values.Clear();
            TimeMs = -1;
            PointDataUtils.EscapeKey(Tags, measurement);
            Tags.Append(_defaultTags);
            return this;
        }

        public void Dispose()
        {
            if (HasValues)
            {
                Debug.Assert(TimeMs >= 0, "Time isn't set");
                Values.Remove(Values.Length - 1, 1);
                Tags.Append(" ").Append(Values).Append(" ").Append(TimeMs);
                _consumer(Tags);
            }

            Reset("");
        }

        public void WriteTag(MetricTag tag, bool safeKey)
        {
            if (tag.Valid)
                PointDataUtils.WriteTag(Tags, tag.Key, tag.Value, safeKey);
        }

        public void WriteTag(string key, string value, bool safeKey)
        {
            PointDataUtils.WriteTag(Tags, key, value, safeKey);
        }

        private void WriteValHeader(string key, bool safeKey)
        {
            if (safeKey)
                PointDataUtils.EscapeKey(Values, key);
            else
                Values.Append(key);
            Values.Append('=');
        }

        public void WriteVal(string key, bool value, bool safeKey)
        {
            WriteValHeader(key, safeKey);
            Values.Append(value ? "1i" : "0i").Append(",");
        }

        public void WriteVal(string key, double value, bool safeKey)
        {
            WriteValHeader(key, safeKey);
            Values.Append(value).Append(",");
        }

        public void WriteVal(string key, long value, bool safeKey)
        {
            WriteValHeader(key, safeKey);
            Values.Append(value).Append("i,");
        }

        public void WriteVal(string key, string value, bool safeKey)
        {
            if (string.IsNullOrEmpty(value))
                return;
            WriteValHeader(key, safeKey);
            Values.Append('"');
            PointDataUtils.EscapeValue(Values, value);
            Values.Append("\",");
        }
    }
}