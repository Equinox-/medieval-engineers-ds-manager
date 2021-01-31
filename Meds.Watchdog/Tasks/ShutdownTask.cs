using System;
using System.Threading.Tasks;
using Meds.Shared.Data;

namespace Meds.Watchdog.Tasks
{
    public sealed class ShutdownTask : ITask
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        
        private readonly Program _program;

        public ShutdownTask(Program program)
        {
            _program = program;
        }

        public async Task Execute()
        {
            SendShutdownRequest();
            var process = _program.HealthTracker.ActiveProcess;
            var start = DateTime.UtcNow;
            while (true)
            {
                if (process == null || process.HasExited)
                    return;
                if ((DateTime.UtcNow - start).TotalSeconds > _program.Configuration.ShutdownTimeout)
                {
                    process.Kill();
                    break;
                }
                await Task.Delay(PollInterval);
            }
        }

        private void SendShutdownRequest()
        {
            var buffer = _program.Channel.SendBuffer;
            var builder = buffer.Builder;
            ShutdownRequest.StartShutdownRequest(builder);
            buffer.EndMessage(Message.ShutdownRequest);
            buffer.Flush();
        }
    }
}