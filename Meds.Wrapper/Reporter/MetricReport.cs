using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Meds.Wrapper.Metrics;

namespace Meds.Wrapper.Reporter
{
    public sealed class MetricReport : IDisposable
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(12);
        private readonly System.Threading.Timer _timer;

        private long _reportTick;

        public MetricReport()
        {
            _reportTick = 0;
            _timer = new System.Threading.Timer(Report);
            _timer.Change(0, (int) ReportInterval.TotalMilliseconds);
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void Report(object state)
        {
            _reportTick++;
            var buffer = Program.Instance.Channel.SendBuffer;
            var reader = MetricRegistry.Read();
            for (var i = 0; i < reader.Count; i++)
            {
                var metric = reader[i];
                if ((_reportTick % metric.UpdateRate) != 0)
                    continue;
                metric.WriteTo(buffer);
            }

            buffer.Flush();
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}