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
    /// An enumerator of <see cref="HistogramIterationValue"/> through the histogram using a <see cref="AllValuesEnumerator"/>
    /// </summary>
    public readonly struct AllValueEnumerable : IEnumerable<HistogramIterationValue>
    {
        private readonly HistogramBase _histogram;

        /// <summary>
        /// The constructor for the <see cref="AllValueEnumerable"/>
        /// </summary>
        /// <param name="histogram">The <see cref="HistogramBase"/> to enumerate the values from.</param>
        public AllValueEnumerable(HistogramBase histogram)
        {
            _histogram = histogram;
        }

        public AllValuesEnumerator GetEnumerator() => new AllValuesEnumerator(_histogram);

        IEnumerator<HistogramIterationValue> IEnumerable<HistogramIterationValue>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}