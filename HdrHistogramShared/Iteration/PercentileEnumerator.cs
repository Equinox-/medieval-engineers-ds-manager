/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System;
using System.Collections;
using System.Collections.Generic;

namespace HdrHistogram.Iteration
{
    /// <summary>
    /// Used for iterating through histogram values according to percentile levels.The iteration is
    /// performed in steps that start at 0% and reduce their distance to 100% according to the
    /// <i>percentileTicksPerHalfDistance</i> parameter, ultimately reaching 100% when all recorded histogram
    /// values are exhausted.
    /// </summary>
    public struct PercentileEnumerator : IEnumerator<HistogramIterationValue>, IHistogramEnumeratorImpl
    {
        private readonly int _percentileTicksPerHalfDistance;
        private double _percentileLevelToIterateTo;
        private bool _reachedLastRecordedValue;
        private AbstractHistogramEnumerator _abstract;

        /// <summary>
        /// The constructor for the <see cref="PercentileEnumerator"/>
        /// </summary>
        /// <param name="histogram">The histogram this iterator will operate on</param>
        /// <param name="percentileTicksPerHalfDistance">The number of iteration steps per half-distance to 100%.</param>
        public PercentileEnumerator(HistogramBase histogram, int percentileTicksPerHalfDistance)
        {
            _percentileTicksPerHalfDistance = percentileTicksPerHalfDistance;
            _abstract = new AbstractHistogramEnumerator(histogram);
            _percentileLevelToIterateTo = 0.0;
            _reachedLastRecordedValue = false;
            Current = default;
        }

        public void Reset()
        {
            _abstract = new AbstractHistogramEnumerator(_abstract.SourceHistogram);
            _percentileLevelToIterateTo = 0.0;
            _reachedLastRecordedValue = false;
        }

        private bool HasNext()
        {
            if (_abstract.HasNext())
                return true;
            if (_reachedLastRecordedValue || _abstract.ArrayTotalCount <= 0) return false;
            // We want one additional last step to 100%
            _percentileLevelToIterateTo = 100.0;
            _reachedLastRecordedValue = true;
            return true;
        }

        public void IncrementIterationLevel()
        {
            var percentileReportingTicks =
                _percentileTicksPerHalfDistance *
                (long) Math.Pow(2,
                    (long) (Math.Log(100.0 / (100.0 - (_percentileLevelToIterateTo))) / Math.Log(2)) + 1);
            _percentileLevelToIterateTo += 100.0 / percentileReportingTicks;
        }

        public bool ReachedIterationLevel()
        {
            if (_abstract.CountAtThisValue == 0)
                return false;
            var currentPercentile = (100.0 * _abstract.TotalCountToCurrentIndex) / _abstract.ArrayTotalCount;
            return (currentPercentile >= _percentileLevelToIterateTo);
        }

        public double GetPercentileIteratedTo() => _percentileLevelToIterateTo;

        public HistogramIterationValue Current { get; private set; }

        public bool MoveNext()
        {
            if (!HasNext()) return false;
            Current = _abstract.Next(ref this);
            return true;
        }

        object IEnumerator.Current => Current;

        public void Dispose()
        {
        }
    }
}