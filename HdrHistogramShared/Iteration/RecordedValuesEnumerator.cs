/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System.Collections;
using System.Collections.Generic;

namespace HdrHistogram.Iteration
{
    /// <summary>
    /// An enumerator that enumerate over all non-zero values.
    /// </summary>
    public struct RecordedValuesEnumerator : IEnumerator<HistogramIterationValue>, IHistogramEnumeratorImpl
    {
        private int _visitedSubBucketIndex;
        private int _visitedBucketIndex;
        private AbstractHistogramEnumerator _abstract;

        /// <summary>
        /// The constructor for <see cref="RecordedValuesEnumerator"/>
        /// </summary>
        /// <param name="histogram">The histogram this iterator will operate on</param>
        public RecordedValuesEnumerator(HistogramBase histogram)
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
            var currentIndexCount = _abstract.SourceHistogram.GetCountAt(_abstract.CurrentBucketIndex, _abstract.CurrentSubBucketIndex);
            return currentIndexCount != 0 && (_visitedSubBucketIndex != _abstract.CurrentSubBucketIndex || _visitedBucketIndex != _abstract.CurrentBucketIndex);
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