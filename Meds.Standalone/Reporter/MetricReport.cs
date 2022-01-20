using System;
using System.Runtime.CompilerServices;
using Meds.Metrics;
using Meds.Standalone.Output;

namespace Meds.Standalone.Reporter
{
    public sealed class MetricReport : IDisposable
    {
#if DEBUG
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(15);
#else
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);
#endif
        private readonly System.Threading.Timer _timer;
        private readonly InfluxMetricWriter _sink;

        private long _reportTick;

        public MetricReport(Influx sink)
        {
            _sink = new InfluxMetricWriter(sink);
            _reportTick = 0;
            _timer = new System.Threading.Timer(Report);
            _timer.Change(0, (int) ReportInterval.TotalMilliseconds);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void Report(object state)
        {
            _reportTick++;
            var reader = MetricRegistry.Read();
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < reader.Count; i++)
            {
                var metric = reader[i];
                if ((_reportTick % metric.UpdateRate) != 0)
                    continue;
                metric.WriteTo(_sink);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}