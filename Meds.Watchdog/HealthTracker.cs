using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meds.Watchdog
{
    public sealed class HealthTracker : IHostedService
    {
        public static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(1);

        private readonly BoolState _liveness = new BoolState();
        private readonly BoolState _readiness = new BoolState();
        private readonly ISubscriber<HealthState> _healthSubscriber;
        private readonly Configuration _config;
        private readonly ILogger<HealthTracker> _log;

        public int PlayerCount { get; private set; }
        public float SimulationSpeed { get; private set; }

        public HealthTracker(ISubscriber<HealthState> healthSubscriber, 
            Configuration config, ILogger<HealthTracker> log)
        {
            _healthSubscriber = healthSubscriber;
            _config = config;
            _log = log;
        }

        public IBoolState Liveness => _liveness;

        public IBoolState Readiness => _readiness;

        public bool IsRunning => !(FindActiveProcess()?.HasExited ?? true);

        public Process ActiveProcess => FindActiveProcess();

        public void Reset()
        {
            _liveness.Reset();
            _readiness.Reset();
        }

        private Process FindActiveProcess()
        {
            var expectedPath = Path.GetFullPath(Path.Combine(_config.InstallDirectory, _config.EntryPoint));
            var processName = Path.GetFileNameWithoutExtension(expectedPath);
            var processes = Process.GetProcessesByName(processName);
            foreach (var proc in processes)
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path != null && Path.GetFullPath(path) == expectedPath)
                        return proc;
                }
                catch
                {
                    // ignore
                }
            }

            return null;
        }

        public interface IBoolState
        {
            bool State { get; }
            DateTime UpdatedAt { get; }
            DateTime ChangedAt { get; }
            TimeSpan TimeInState { get; }

            bool IsCurrent { get; }
        }

        private sealed class BoolState : IBoolState
        {
            private bool _state;
            public DateTime UpdatedAt { get; private set; }
            public DateTime ChangedAt { get; private set; }
            public TimeSpan TimeInState => DateTime.UtcNow - ChangedAt;
            public bool IsCurrent => (UpdatedAt + HealthTimeout) >= DateTime.UtcNow;

            public BoolState()
            {
                Reset();
            }

            public bool State => _state;

            public bool UpdateState(bool newState)
            {
                UpdatedAt = DateTime.UtcNow;
                if (_state == newState)
                    return false;
                ChangedAt = DateTime.UtcNow;
                _state = newState;
                return true;
            }

            public void Reset()
            {
                _state = false;
                var now = DateTime.UtcNow;
                UpdatedAt = now;
                ChangedAt = now;
            }
        }


        private IDisposable _subscription;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _subscription = _healthSubscriber.Subscribe(msg =>
            {
                if (_readiness.UpdateState(msg.Readiness))
                    _log.LogInformation("Readiness changed from {Previous} to {Current}", !msg.Readiness, msg.Readiness);
                if (_liveness.UpdateState(msg.Liveness)) 
                    _log.LogInformation("Liveness changed from {Previous} to {Current}", !msg.Liveness, msg.Liveness);

                PlayerCount = msg.Players;
                SimulationSpeed = msg.SimSpeed;
            });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _subscription.Dispose();
            return Task.CompletedTask;
        }
    }
}