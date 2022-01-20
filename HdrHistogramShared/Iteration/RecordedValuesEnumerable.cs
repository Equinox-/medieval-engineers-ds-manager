/*
 * This is a .NET port of the original Java version, which was written by
 * Gil Tene as described in
 * https://github.com/HdrHistogram/HdrHistogram
 * and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */

using System.Collections;
using System.Collections.Generic;
using Meds.Standalone.Iteration;

namespace HdrHistogram.Iteration
{
    /// <summary>
    /// An enumerator of <see cref="HistogramIterationValue"/> through the histogram using a <see cref="RecordedValuesEnumerator"/>
    /// </summary>
    public readonly struct RecordedValuesEnumerable : IEnumerable<HistogramIterationValue>
    {
        private readonly HistogramBase _histogram;

        public RecordedValuesEnumerable(HistogramBase histogram)
        {
            _histogram = histogram;
        }

        public RecordedValuesEnumerator GetEnumerator() => new RecordedValuesEnumerator(_histogram);

        IEnumerator<HistogramIterationValue> IEnumerable<HistogramIterationValue>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}