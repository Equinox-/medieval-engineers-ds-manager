using System;
using System.IO;
using Meds.Shared;
using Meds.Shared.Data;
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
            var histogram = MetricRegistry.HistogramFactory.Create();
            histogram.RecordValueWithCount(10, 4);
            histogram.RecordValueWithCount(100, 2);
            histogram.RecordValueWithCount(1000, 1);
            var reader = HistogramReader.Read(histogram);
            Console.WriteLine(reader);
        }
    }
}