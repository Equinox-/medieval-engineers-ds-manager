using System;
using Meds.Standalone.Output;

namespace Meds.Standalone.Reporter
{
    public sealed class HealthReport : IDisposable
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(ReportInterval.TotalSeconds * 4);

        private readonly System.Threading.Timer _timer;
        public DateTime LastGameTick;
        private readonly Influx _sink;

        public HealthReport(Influx sink)
        {
            _sink = sink;
            _timer = new System.Threading.Timer(Report);
            _timer.Change(0, (int) ReportInterval.TotalMilliseconds);
        }

        private void Report(object state)
        {
            using (var writer = _sink.Write("meds.health"))
            {
                writer.TimeMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                writer.WriteVal("liveness", true, true);
                writer.WriteVal("readiness", LastGameTick + ReadinessTimeout >= DateTime.UtcNow, true);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}