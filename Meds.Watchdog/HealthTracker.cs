using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class HealthTracker : IHostedService
    {
        public static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(1);

        private readonly BoolState _liveness = new BoolState();
        private readonly BoolState _readiness = new BoolState();
        private readonly ISubscriber<HealthState> _healthSubscriber;
        private readonly Refreshable<Configuration> _config;
        private readonly InstallConfiguration _installConfig;
        private readonly ILogger<HealthTracker> _log;

        public int PlayerCount { get; private set; }
        public float SimulationSpeed { get; private set; }

        public VersionInfo? Version { get; private set; }

        public readonly struct VersionInfo
        {
            public readonly DateTime CompiledAtUtc;
            public readonly string GitHash;
            public readonly string Medieval;

            public VersionInfo(VersionInfoMsg msg)
            {
                CompiledAtUtc = new DateTime(msg.CompiledAt, DateTimeKind.Utc);
                GitHash = msg.GitHash;
                Medieval = msg.Medieval;
            }
        }

        public HealthTracker(ISubscriber<HealthState> healthSubscriber,
            InstallConfiguration installConfig,
            Refreshable<Configuration> config, ILogger<HealthTracker> log)
        {
            _healthSubscriber = healthSubscriber;
            _installConfig = installConfig;
            _config = config;
            _log = log;
        }

        public IBoolState Liveness => _liveness;

        public IBoolState Readiness => _readiness;

        private Process _lastProcess;

        public bool IsRunning => !(ActiveProcess?.HasExited ?? true);

        public Process ActiveProcess
        {
            get
            {
                if (_lastProcess is { HasExited: false })
                    return _lastProcess;
                return _lastProcess = FindActiveProcess();
            }
        }

        public void Reset()
        {
            _liveness.Reset();
            _readiness.Reset();
        }

        private Process FindActiveProcess()
        {
            var expectedPath = Path.GetFullPath(Path.Combine(_installConfig.InstallDirectory, _config.Current.WrapperEntryPoint));
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
                    _log.ZLogInformation("Readiness changed from {0} to {1}", !msg.Readiness, msg.Readiness);
                if (_liveness.UpdateState(msg.Liveness))
                    _log.ZLogInformation("Liveness changed from {0} to {1}", !msg.Liveness, msg.Liveness);

                PlayerCount = msg.Players;
                SimulationSpeed = msg.SimSpeed;
                var ver = msg.Version;
                Version = ver != null ? (VersionInfo?) new VersionInfo(ver.Value) : null;
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