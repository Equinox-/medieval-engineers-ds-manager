using System;
using System.IO;
using HdrHistogram;
using Meds.Metrics;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Wrapper;
using Meds.Wrapper.Metrics;
using NUnit.Framework;

namespace UnitTests
{
    [TestFixture]
    public class HistogramTests
    {
        [Test]
        public void Test()
        {
            var histogram = new IntHistogram(HistogramMetricBase.LowestTrackableValue, HistogramMetricBase.HighestTrackableValue,
                HistogramMetricBase.NumberOfSignificantValueDigits);
            histogram.RecordValueWithCount(10, 4);
            histogram.RecordValueWithCount(100, 2);
            histogram.RecordValueWithCount(1000, 1);
            var reader = HistogramReader.Read(histogram);
            Console.WriteLine(reader.ToString());
        }
    }
}