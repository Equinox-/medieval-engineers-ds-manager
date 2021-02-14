using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Watchdog.Utils
{
    public sealed class HealthTracker : IDisposable
    {
        private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(1);
        private readonly Program _program;
        private readonly Timer _timer;

        private readonly BoolState _liveness = new BoolState(false);
        private readonly BoolState _readiness = new BoolState(false);

        public HealthTracker(Program pgm)
        {
            _program = pgm;
            pgm.Distributor.RegisterPacketHandler(HandleMessage, Message.HealthState);
            _timer = new Timer(Report);
            _timer.Change(0, 15 * 1000);
        }

        private void Report(object state)
        {
            using (var writer = _program.Influx.Write("meds.health"))
            {
                writer.TimeMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                writer.WriteVal("running", ActiveProcess != null, true);
                writer.WriteVal("liveness", _liveness.State, true);
                writer.WriteVal("readiness", _readiness.State, true);
            }
        }

        private void HandleMessage(PacketDistributor.MessageToken obj)
        {
            var state = obj.Value<HealthState>();

            _liveness.State = state.Liveness;
            _readiness.State = state.Readiness;
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
            var expectedPath = Path.GetFullPath(Path.Combine(_program.InstallDirectory, _program.Configuration.EntryPoint));
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

            public BoolState(bool initial)
            {
                _state = initial;
                var now = DateTime.UtcNow;
                ChangedAt = now;
                UpdatedAt = now;
            }

            public bool State
            {
                get => _state;
                set
                {
                    UpdatedAt = DateTime.UtcNow;
                    if (_state == value) return;
                    ChangedAt = DateTime.UtcNow;
                    _state = value;
                }
            }

            public void Reset()
            {
                _state = false;
                UpdatedAt = default;
                ChangedAt = default;
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}