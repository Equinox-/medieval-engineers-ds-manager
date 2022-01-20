/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System;
using System.Linq;
using HdrHistogram.Iteration;
using Meds.Standalone.Iteration;

namespace HdrHistogram
{
    /// <summary>
    /// Extension methods for the Histogram types.
    /// </summary>
    public static class HistogramExtensions
    {

        /// <summary>
        /// Get the highest recorded value level in the histogram
        /// </summary>
        /// <returns>the Max value recorded in the histogram</returns>
        public static long GetMaxValue(this HistogramBase histogram)
        {
            var max = histogram.RecordedValues().Select(hiv => hiv.ValueIteratedTo).LastOrDefault();
            return histogram.HighestEquivalentValue(max);
        }

        /// <summary>
        /// Get the computed mean value of all recorded values in the histogram
        /// </summary>
        /// <returns>the mean value (in value units) of the histogram data</returns>
        public static double GetMean(this HistogramBase histogram)
        {
            var totalValue = histogram.RecordedValues().Select(hiv => hiv.TotalValueToThisValue).LastOrDefault();
            return (totalValue * 1.0) / histogram.TotalCount;
        }

        /// <summary>
        /// Get the computed standard deviation of all recorded values in the histogram
        /// </summary>
        /// <returns>the standard deviation (in value units) of the histogram data</returns>
        public static double GetStdDeviation(this HistogramBase histogram)
        {
            var mean = histogram.GetMean();
            var geometricDeviationTotal = 0.0;
            foreach (var iterationValue in histogram.RecordedValues())
            {
                double deviation = (histogram.MedianEquivalentValue(iterationValue.ValueIteratedTo) * 1.0) - mean;
                geometricDeviationTotal += (deviation * deviation) * iterationValue.CountAddedInThisIterationStep;
            }
            var stdDeviation = Math.Sqrt(geometricDeviationTotal / histogram.TotalCount);
            return stdDeviation;
        }

        /// <summary>
        /// Get the highest value that is equivalent to the given value within the histogram's resolution.
        /// Where "equivalent" means that value samples recorded for any two equivalent values are counted in a common
        /// total count.
        /// </summary>
        /// <param name="histogram">The histogram to operate on</param>
        /// <param name="value">The given value</param>
        /// <returns>The highest value that is equivalent to the given value within the histogram's resolution.</returns>
        public static long HighestEquivalentValue(this HistogramBase histogram, long value)
        {
            return histogram.NextNonEquivalentValue(value) - 1;
        }

        /// <summary>
        /// Copy this histogram into the target histogram, overwriting it's contents.
        /// </summary>
        /// <param name="source">The source histogram</param>
        /// <param name="targetHistogram">the histogram to copy into</param>
        public static void CopyInto(this HistogramBase source, HistogramBase targetHistogram)
        {
            targetHistogram.Reset();
            targetHistogram.Add(source);
            targetHistogram.StartTimeStamp = source.StartTimeStamp;
            targetHistogram.EndTimeStamp = source.EndTimeStamp;
        }

        /// <summary>
        /// Provide a means of iterating through histogram values according to percentile levels. 
        /// The iteration is performed in steps that start at 0% and reduce their distance to 100% according to the
        /// <paramref name="percentileTicksPerHalfDistance"/> parameter, ultimately reaching 100% when all recorded
        /// histogram values are exhausted.
        /// </summary>
        /// <param name="histogram">The histogram to operate on</param>
        /// <param name="percentileTicksPerHalfDistance">
        /// The number of iteration steps per half-distance to 100%.
        /// </param>
        /// <returns>
        /// An enumerator of <see cref="HistogramIterationValue"/> through the histogram using a
        /// <see cref="PercentileEnumerator"/>.
        /// </returns>
        public static PercentileEnumerable Percentiles(this HistogramBase histogram, int percentileTicksPerHalfDistance)
        {
            return new PercentileEnumerable(histogram, percentileTicksPerHalfDistance);
        }
    }
}