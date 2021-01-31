using System;
using System.Threading;
using Meds.Shared.Data;

namespace Meds.Wrapper.Reporter
{
    public sealed class HealthReport : IDisposable
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(ReportInterval.TotalSeconds * 4);
        
        private readonly Timer _timer;
        public DateTime LastGameTick;

        public HealthReport()
        {
            _timer = new Timer(Report);
            _timer.Change(0, 15 * 1000); // 15 sec
        }

        private void Report(object state)
        {
            var buffer = Program.Instance.Channel.SendBuffer;
            var builder = buffer.Builder;
            HealthState.StartHealthState(builder);
            HealthState.AddLiveness(builder, true);
            HealthState.AddReadiness(builder, LastGameTick + ReadinessTimeout >= DateTime.UtcNow);
            buffer.EndMessage(Message.HealthState);
            buffer.Flush();
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}