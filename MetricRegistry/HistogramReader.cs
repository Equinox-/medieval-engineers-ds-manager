using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HdrHistogram;
using HdrHistogram.Iteration;

namespace Meds.Metrics
{
    public sealed class HistogramReader
    {
        private static readonly List<KeyValuePair<double, Action<HistogramReader, long>>> PercentileSetters =
            new SortedDictionary<double, Action<HistogramReader, long>>
            {
                [50] = (r, v) => r.P50 = v,
                [75] = (r, v) => r.P75 = v,
                [90] = (r, v) => r.P90 = v,
                [95] = (r, v) => r.P95 = v,
                [98] = (r, v) => r.P98 = v,
                [99] = (r, v) => r.P99 = v,
                [99.9] = (r, v) => r.P999 = v,
            }.ToList();

        private static readonly ThreadLocal<HistogramReader>
            Instances = new ThreadLocal<HistogramReader>(() => new HistogramReader(new IntHistogram(HistogramMetricBase.LowestTrackableValue,
                HistogramMetricBase.HighestTrackableValue, HistogramMetricBase.NumberOfSignificantValueDigits)));

        private RecordedValuesEnumerator _histogramRecorded;

        private HistogramReader(HistogramBase histogram)
        {
            Histogram = histogram;
            _histogramRecorded = Histogram.RecordedValues().GetEnumerator();
        }

        public static HistogramReader Read(HistogramBase src)
        {
            var reader = Instances.Value;
            src.CopyInto(reader.Histogram);
            src.Reset();
            reader.UpdateFields();
            return reader;
        }

        public static HistogramReader Read(Recorder src)
        {
            var reader = Instances.Value;
            src.GetIntervalHistogramInto(reader.Histogram);
            reader.UpdateFields();
            return reader;
        }

        public HistogramBase Histogram { get; }

        public long SampleCount { get; private set; }
        public long Min { get; private set; }
        public long Max { get; private set; }
        public double Mean { get; private set; }
        public double StdDev { get; private set; }

        public long P50 { get; private set; }
        public long P75 { get; private set; }
        public long P90 { get; private set; }
        public long P95 { get; private set; }
        public long P98 { get; private set; }
        public long P99 { get; private set; }
        public long P999 { get; private set; }

        private void UpdateFields()
        {
            HistogramIterationValue? last = null;
            _histogramRecorded.Reset();
            for (var itr = _histogramRecorded; itr.MoveNext();)
            {
                var val = itr.Current;
                if (last == null)
                    Min = Histogram.LowestEquivalentValue(val.ValueIteratedTo);
                last = itr.Current;
            }

            if (last == null || last.Value.TotalCountToThisValue <= 0)
            {
                SampleCount = 0;
                Min = 0;
                Max = 0;
                Mean = 0;
                StdDev = 0;
                return;
            }

            SampleCount = last.Value.TotalCountToThisValue;
            Max = Histogram.HighestEquivalentValue(last.Value.ValueIteratedTo);
            Mean = last.Value.TotalValueToThisValue / (double)last.Value.TotalCountToThisValue;
            StdDev = ComputeStdDev(Mean);

            var currPercentile = 0;
            _histogramRecorded.Reset();
            for (var itr = _histogramRecorded; currPercentile < PercentileSetters.Count && itr.MoveNext();)
            {
                var val = itr.Current;
                var target = PercentileSetters[currPercentile];
                if (val.Percentile < target.Key) continue;
                target.Value(this, val.ValueIteratedTo);
                currPercentile++;
            }

            while (currPercentile < PercentileSetters.Count)
                PercentileSetters[currPercentile++].Value(this, Max);
        }

        private double ComputeStdDev(double mean)
        {
            var stdDevAccumulator = 0.0;
            var totalCount = 0L;
            _histogramRecorded.Reset();
            for (var itr = _histogramRecorded; itr.MoveNext();)
            {
                var val = itr.Current;
                var error = Histogram.MedianEquivalentValue(val.ValueIteratedTo) - mean;
                stdDevAccumulator += error * error * val.CountAddedInThisIterationStep;
                totalCount = val.TotalCountToThisValue;
            }

            return Math.Sqrt(stdDevAccumulator / totalCount);
        }

        public override string ToString() =>
            $"min={Min} mean={Mean} p50={P50} p75={P75} p90={P90} p95={P95} p98={P98} p99={P99} p99.9={P999} max={Max} stddev={StdDev} count={SampleCount}";
    }
}