using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Meds.Metrics;
using Meds.Metrics.Group;

namespace Meds.Standalone.Output.Prometheus
{
    public sealed class PrometheusMetricWriter : MetricWriter
    {
        private readonly TextWriter _writer;
        private readonly HashSet<KeyValuePair<string, string>> _firstEncounter = new HashSet<KeyValuePair<string, string>>();

        [ThreadStatic]
        private static char[] NameBuffer;

        [ThreadStatic]
        private static char[] LabelBuffer;

        [ThreadStatic]
        private static char[] SuffixBuffer;

        public PrometheusMetricWriter(TextWriter writer)
        {
            _writer = writer;
        }

        private static char MakeSafeIdentifier(char chr)
        {
            if (chr >= 'a' && chr <= 'z')
                return chr;
            if (chr >= 'A' && chr <= 'Z')
                return (char)(chr - 'A' + 'a');
            return '_';
        }

        private static void FillNameBuffer(string name, string safeExtra, char[] buffer, out int len)
        {
            len = 0;
            foreach (var c in safeExtra)
                buffer[len++] = c;
            var wasLower = false;
            foreach (var c in name)
            {
                // camelCase to snake_case
                if (wasLower && c >= 'A' && c <= 'Z') buffer[len++] = '_';
                var safe = MakeSafeIdentifier(c);
                buffer[len++] = safe;
                wasLower = c >= 'a' && c <= 'z';
            }
        }

        private readonly struct PreparedName
        {
            public readonly int NameLength;
            public readonly char[] Name;
            public readonly int LabelLength;
            public readonly char[] Labels;

            private static void AppendLabel(in MetricTag tag, char[] labels, ref int pos)
            {
                if (!tag.Valid) return;
                if (pos == 0)
                    labels[pos++] = '{';
                else
                    labels[pos++] = ',';
                foreach (var c in tag.Key)
                    labels[pos++] = MakeSafeIdentifier(c);
                labels[pos++] = '=';
                labels[pos++] = '"';
                foreach (var c in tag.Value)
                {
                    if (c == '\\' || c == '"' || c == '\n')
                        labels[pos++] = '\\';
                    labels[pos++] = c == '\n' ? 'n' : c;
                }

                labels[pos++] = '"';
            }

            public PreparedName(in MetricName name)
            {
                Name = NameBuffer ?? (NameBuffer = new char[256]);
                FillNameBuffer(name.Series, "", Name, out NameLength);
                var labelsLen = 0;
                Labels = LabelBuffer ?? (LabelBuffer = new char[8192]);
                AppendLabel(in name.Kv0, Labels, ref labelsLen);
                AppendLabel(in name.Kv1, Labels, ref labelsLen);
                AppendLabel(in name.Kv2, Labels, ref labelsLen);
                AppendLabel(in name.Kv3, Labels, ref labelsLen);
                AppendLabel(in name.Kv4, Labels, ref labelsLen);
                if (labelsLen > 0) Labels[labelsLen++] = '}';
                LabelLength = labelsLen;
            }

            public void WriteNameOnly(TextWriter writer) => writer.Write(Name, 0, NameLength);

            public void WriteLabelsOnly(TextWriter writer) => writer.Write(Labels, 0, LabelLength);
        }

        public void WriteGroup<T>(in MetricName name, T reader) where T : IEnumerator<KeyValuePair<string, LeafMetricValue>>
        {
            var preparedName = new PreparedName(in name);
            while (reader.MoveNext())
            {
                var kv = reader.Current;
                var key = kv.Key;
                var value = kv.Value;
                var suffixBuffer = SuffixBuffer ?? (SuffixBuffer = new char[64]);
                FillNameBuffer(key, "_", SuffixBuffer, out var suffixLength);
                if (_firstEncounter.Add(new KeyValuePair<string, string>(name.Series, key)))
                {
                    _writer.Write("# TYPE ");
                    preparedName.WriteNameOnly(_writer);
                    _writer.Write(suffixBuffer, 0, suffixLength);
                    switch (value.Type)
                    {
                        case LeafMetricType.Counter:
                            _writer.WriteLine(" counter");
                            break;
                        case LeafMetricType.Gauge:
                        case LeafMetricType.PerTickAdder:
                            _writer.WriteLine(" gauge");
                            break;
                        default:
                            _writer.WriteLine(" untyped");
                            break;
                    }
                }

                preparedName.WriteNameOnly(_writer);
                _writer.Write(suffixBuffer, 0, suffixLength);
                preparedName.WriteLabelsOnly(_writer);
                _writer.Write(' ');
                switch (value.Type)
                {
                    case LeafMetricType.Counter:
                        _writer.WriteLine(value.LongValue);
                        break;
                    case LeafMetricType.Gauge:
                    case LeafMetricType.PerTickAdder:
                        _writer.WriteLine(value.DoubleValue);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public void WriteHistogram(in MetricName name, double min, double mean, double p50, double p75, double p90, double p95, double p98, double p99,
            double p999, double max, double stdDev, long count)
        {
            var preparedName = new PreparedName(in name);
            if (_firstEncounter.Add(new KeyValuePair<string, string>(name.Series, "")))
            {
                _writer.Write("# TYPE ");
                preparedName.WriteNameOnly(_writer);
                _writer.WriteLine(" summary");
            }

            void WriteQuantile(string quantile, double value)
            {
                preparedName.WriteNameOnly(_writer);
                if (preparedName.LabelLength > 0)
                {
                    _writer.Write(preparedName.Labels, 0, preparedName.LabelLength - 1);
                    _writer.Write(",quantile=\"");
                    _writer.Write(quantile);
                    _writer.Write("\"} ");
                }
                else
                {
                    _writer.Write("{quantile=\"");
                    _writer.Write(quantile);
                    _writer.Write("\"} ");
                }

                _writer.WriteLine(value);
            }

            WriteQuantile("0", min);
            WriteQuantile("0.5", p50);
            WriteQuantile("0.75", p75);
            WriteQuantile("0.9", p90);
            WriteQuantile("0.95", p95);
            WriteQuantile("0.98", p98);
            WriteQuantile("0.99", p99);
            WriteQuantile("0.999", p999);
            WriteQuantile("1", max);

            void WriteHeader(string suffix)
            {
                preparedName.WriteNameOnly(_writer);
                _writer.Write(suffix);
                preparedName.WriteLabelsOnly(_writer);
                _writer.Write(" ");
            }

            WriteHeader("_count");
            _writer.WriteLine(count);

            WriteHeader("_sum");
            _writer.WriteLine(count * mean);
        }
    }
}