/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System;
using HdrHistogram;

namespace HdrHistogram.Iteration
{
    internal interface IHistogramEnumeratorImpl
    {
        void IncrementIterationLevel();

        bool ReachedIterationLevel();

        // return (100.0 * TotalCountToCurrentIndex) / ArrayTotalCount;
        double GetPercentileIteratedTo();
    }

    /// <summary>
    /// Provide functionality for enumerating over histogram counts.
    /// </summary>
    internal struct AbstractHistogramEnumerator
    {
        private long _savedHistogramTotalRawCount;
        private int _nextBucketIndex;
        private int _nextSubBucketIndex;
        private long _prevValueIteratedTo;
        private long _totalCountToPrevIndex;
        private long _totalValueToCurrentIndex;
        private bool _freshSubBucket;
        private long _currentValueAtIndex;
        private long _nextValueAtIndex;

        public readonly HistogramBase SourceHistogram;
        internal long ArrayTotalCount;
        internal int CurrentBucketIndex;
        internal int CurrentSubBucketIndex;
        internal long TotalCountToCurrentIndex;
        internal long CountAtThisValue;

        internal AbstractHistogramEnumerator(HistogramBase histogram)
        {
            SourceHistogram = histogram;
            _savedHistogramTotalRawCount = SourceHistogram.TotalCount;
            ArrayTotalCount = SourceHistogram.TotalCount;
            CurrentBucketIndex = 0;
            CurrentSubBucketIndex = 0;
            _currentValueAtIndex = 0;
            _nextBucketIndex = 0;
            _nextSubBucketIndex = 1;
            _nextValueAtIndex = 1;
            _prevValueIteratedTo = 0;
            _totalCountToPrevIndex = 0;
            TotalCountToCurrentIndex = 0;
            _totalValueToCurrentIndex = 0;
            CountAtThisValue = 0;
            _freshSubBucket = true;
        }

        /// <summary>
        ///  Returns <c>true</c> if the iteration has more elements. (In other words, returns true if next would return an element rather than throwing an exception.)
        /// </summary>
        /// <returns><c>true</c> if the iterator has more elements.</returns>
        internal bool HasNext()
        {
            if (SourceHistogram.TotalCount != _savedHistogramTotalRawCount)
            {
                throw new InvalidOperationException("Source has been modified during enumeration.");
            }

            return (TotalCountToCurrentIndex < ArrayTotalCount);
        }

        private long GetValueIteratedTo() => SourceHistogram.HighestEquivalentValue(_currentValueAtIndex);

        internal double PercentileIteratedToDefault => 100.0 * TotalCountToCurrentIndex / ArrayTotalCount;

        /// <summary>
        /// Returns the next element in the iteration.
        /// </summary>
        /// <returns>the <see cref="HistogramIterationValue"/> associated with the next element in the iteration.</returns>
        public HistogramIterationValue Next<T>(ref T impl) where T : struct, IHistogramEnumeratorImpl
        {
            // Move through the sub buckets and buckets until we hit the next reporting level:
            while (!ExhaustedSubBuckets())
            {
                CountAtThisValue = SourceHistogram.GetCountAt(CurrentBucketIndex, CurrentSubBucketIndex);
                if (_freshSubBucket)
                {
                    // Don't add unless we've incremented since last bucket...
                    TotalCountToCurrentIndex += CountAtThisValue;
                    _totalValueToCurrentIndex += CountAtThisValue * SourceHistogram.MedianEquivalentValue(_currentValueAtIndex);
                    _freshSubBucket = false;
                }

                if (impl.ReachedIterationLevel())
                {
                    var valueIteratedTo = GetValueIteratedTo();
                    var result = new HistogramIterationValue(
                        valueIteratedTo,
                        _prevValueIteratedTo,
                        CountAtThisValue,
                        (TotalCountToCurrentIndex - _totalCountToPrevIndex),
                        TotalCountToCurrentIndex,
                        _totalValueToCurrentIndex,
                        ((100.0 * TotalCountToCurrentIndex) / ArrayTotalCount),
                        impl.GetPercentileIteratedTo());
                    _prevValueIteratedTo = valueIteratedTo;
                    _totalCountToPrevIndex = TotalCountToCurrentIndex;
                    // move the next iteration level forward:
                    impl.IncrementIterationLevel();
                    if (SourceHistogram.TotalCount != _savedHistogramTotalRawCount)
                    {
                        throw new InvalidOperationException("Source has been modified during enumeration.");
                    }

                    return result;
                }

                IncrementSubBucket();
            }

            // Should not reach here. But possible for overflowed histograms under certain conditions
            throw new ArgumentOutOfRangeException();
        }

        private bool ExhaustedSubBuckets()
        {
            return (CurrentBucketIndex >= SourceHistogram.BucketCount);
        }

        private void IncrementSubBucket()
        {
            _freshSubBucket = true;
            // Take on the next index:
            CurrentBucketIndex = _nextBucketIndex;
            CurrentSubBucketIndex = _nextSubBucketIndex;
            _currentValueAtIndex = _nextValueAtIndex;
            // Figure out the next next index:
            _nextSubBucketIndex++;
            if (_nextSubBucketIndex >= SourceHistogram.SubBucketCount)
            {
                _nextSubBucketIndex = SourceHistogram.SubBucketHalfCount;
                _nextBucketIndex++;
            }

            _nextValueAtIndex = SourceHistogram.ValueFromIndex(_nextBucketIndex, _nextSubBucketIndex);
        }
    }
}