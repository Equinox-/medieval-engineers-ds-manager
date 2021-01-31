using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Meds.Watchdog.Tasks
{
    public sealed class StartTask : ITask
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private readonly Program _program;

        public StartTask(Program program)
        {
            _program = program;
        }

        public async Task Execute()
        {
            var process = _program.HealthTracker.ActiveProcess;
            if (process != null && !process.HasExited)
                throw new Exception($"Can't start when process {process.ProcessName} is already running");

            _program.HealthTracker.Reset();
            var started = Process.Start(new ProcessStartInfo
            {
                WorkingDirectory = _program.Configuration.Directory,
                Arguments = $"\"{_program.Configuration.Directory.Replace("\"", "\\\"")}\" " +
                            $"\"{_program.Configuration.ChannelName.Replace("\"", "\\\"")}\"",
                FileName = Path.Combine(_program.InstallDirectory, _program.Configuration.EntryPoint)
            });
            var reportedProcess = _program.HealthTracker.ActiveProcess;
            if (started?.Id != reportedProcess?.Id)
                throw new Exception($"Started process {started?.Id} is not the reported process {reportedProcess?.Id}");
            // Wait for process to startup and liveness report
            var start = DateTime.UtcNow;
            while (true)
            {
                if ((DateTime.UtcNow - start).TotalSeconds > _program.Configuration.LivenessTimeout)
                    throw new TimeoutException($"Server did not become alive within {_program.Configuration.LivenessTimeout} seconds");
                if (_program.HealthTracker.IsRunning && _program.HealthTracker.Liveness.IsCurrent && _program.HealthTracker.Liveness.State)
                    return;
                await Task.Delay(PollInterval);
            }
        }
    }
}