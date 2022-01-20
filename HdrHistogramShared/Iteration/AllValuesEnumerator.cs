/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System.Collections;
using System.Collections.Generic;
using HdrHistogram;
using HdrHistogram.Iteration;

namespace Meds.Standalone.Iteration
{
    /// <summary>
    /// Used for iterating through histogram values using the finest granularity steps supported by the underlying
    /// representation.The iteration steps through all possible unit value levels, regardless of whether or not
    /// there were recorded values for that value level, and terminates when all recorded histogram values are exhausted.
    /// </summary>
    public struct AllValuesEnumerator : IEnumerator<HistogramIterationValue>, IHistogramEnumeratorImpl
    {
        private int _visitedSubBucketIndex;
        private int _visitedBucketIndex;
        private AbstractHistogramEnumerator _abstract;

        /// <summary>
        /// Constructor for the <see cref="AllValuesEnumerator"/>.
        /// </summary>
        /// <param name="histogram">The histogram this iterator will operate on</param>
        public AllValuesEnumerator(HistogramBase histogram)
        {
            _abstract = new AbstractHistogramEnumerator(histogram);
            _visitedSubBucketIndex = -1;
            _visitedBucketIndex = -1;
            Current = default;
        }

        public void Reset()
        {
            _abstract = new AbstractHistogramEnumerator(_abstract.SourceHistogram);
            _visitedSubBucketIndex = -1;
            _visitedBucketIndex = -1;
        }

        public void IncrementIterationLevel()
        {
            _visitedSubBucketIndex = _abstract.CurrentSubBucketIndex;
            _visitedBucketIndex = _abstract.CurrentBucketIndex;
        }

        public bool ReachedIterationLevel()
        {
            return _visitedSubBucketIndex != _abstract.CurrentSubBucketIndex || _visitedBucketIndex != _abstract.CurrentBucketIndex;
        }

        public double GetPercentileIteratedTo() => _abstract.PercentileIteratedToDefault;

        public HistogramIterationValue Current { get; private set; }

        public bool MoveNext()
        {
            if (!_abstract.HasNext()) return false;
            Current = _abstract.Next(ref this);
            return true;
        }

        object IEnumerator.Current => Current;

        public void Dispose()
        {
        }
    }
}