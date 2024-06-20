using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;
using ZLogger;

namespace Meds.Watchdog
{
    public class LifecycleController : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CronTabBuffer = TimeSpan.FromSeconds(5);

        private readonly ILogger<LifecycleController> _log;
        private readonly HealthTracker _healthTracker;
        private readonly InstallConfiguration _installConfig;
        private readonly Refreshable<Configuration> _runtimeConfig;

        private DateTime? _startedAt;
        private readonly IPublisher<ShutdownRequest> _shutdownPublisher;
        private readonly IPublisher<ChatMessage> _sendChatMessagePublisher;
        private readonly Updater _updater;
        private readonly ConfigRenderer _configRenderer;
        private readonly Refreshable<List<CrontabEntry>> _scheduled;
        private readonly DiagnosticController _diagnostics;
        private readonly List<TaskCompletionSource<bool>> _pinRequests = new List<TaskCompletionSource<bool>>();
        private readonly DataStore _dataStore;

        private readonly struct CrontabEntry
        {
            public readonly CrontabSchedule Schedule;
            public readonly bool Utc;
            public readonly LifecycleState Target;

            public CrontabEntry(CrontabSchedule schedule, bool utc, LifecycleState target)
            {
                Schedule = schedule;
                Utc = utc;
                Target = target;
            }
        }

        public delegate void DelStateChanged(LifecycleState previousState, LifecycleState currentState);

        public event DelStateChanged StateChanged;

        public delegate void DelStartStop(StartStopEvent type, TimeSpan uptime);

        public event DelStartStop StartStop;

        public enum StartStopEvent
        {
            Starting,
            Started,
            Stopping,
            Stopped,
            Crashed,
            Froze
        }

        private LifecycleState _active;

        public LifecycleState Active
        {
            get => _active;
            private set
            {
                if (value.Equals(_active))
                    return;
                var prev = _active;
                _active = value;
                StateChanged?.Invoke(prev, _active);
                using var tok = _dataStore.Write(out var data);
                tok.Update(ref data.LifecycleState, value);
            }
        }

        private LifecycleStateRequest? _request;
        private string _lastSentRequestMessage;
        
        public LifecycleStateRequest? Request
        {
            get
            {
                var best = _request;
                foreach (var entry in _scheduled.Current)
                {
                    var nextOccurrenceUtc = entry.Utc
                        ? entry.Schedule.GetNextOccurrence(DateTime.UtcNow - CronTabBuffer)
                        : entry.Schedule.GetNextOccurrence(DateTime.Now - CronTabBuffer).ToUniversalTime();
                    if (best == null || best.Value.ActivateAtUtc > nextOccurrenceUtc)
                        best = new LifecycleStateRequest(nextOccurrenceUtc, entry.Target);
                }

                return best;
            }
            set
            {
                _request = value;
                _lastSentRequestMessage = null;
            }
        }

        public LifecycleStateRequest? ProcessingRequest { get; private set; }

        public LifecycleController(ILogger<LifecycleController> logger,
            HealthTracker health,
            InstallConfiguration installConfig,
            Refreshable<Configuration> runtimeConfig,
            IPublisher<ShutdownRequest> shutdownPublisher,
            Updater updater,
            ConfigRenderer configRenderer, IPublisher<ChatMessage> sendChatMessagePublisher, DiagnosticController diagnostics, DataStore dataStore)
        {
            _log = logger;
            _healthTracker = health;
            _installConfig = installConfig;
            _runtimeConfig = runtimeConfig;
            _updater = updater;
            _shutdownPublisher = shutdownPublisher;
            _configRenderer = configRenderer;
            _sendChatMessagePublisher = sendChatMessagePublisher;
            _diagnostics = diagnostics;
            _dataStore = dataStore;
            using (_dataStore.Read(out var data))
                _active = data.LifecycleState != null ? (LifecycleState) data.LifecycleState : new LifecycleState(LifecycleStateCase.Running);

            _scheduled = runtimeConfig
                .Map(cfg => cfg.ScheduledTasks, CollectionEquality<Configuration.ScheduledTaskConfig>.List())
                .Map(tasks =>
                {
                    var dest = new List<CrontabEntry>(tasks?.Count ?? 0);
                    if (tasks == null)
                        return dest;
                    foreach (var task in tasks)
                    {
                        try
                        {
                            var schedule = CrontabSchedule.Parse(task.Cron);
                            dest.Add(new CrontabEntry(schedule, task.Utc, new LifecycleState(task.Target, task.Reason)));
                        }
                        catch (Exception err)
                        {
                            _log.ZLogWarning(err, "Failed to parse crontab {0}", task.Cron);
                        }
                    }

                    return dest;
                });
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var pinRequestsCopy = new List<TaskCompletionSource<bool>>();
            while (!stoppingToken.IsCancellationRequested)
            {
                bool pinned;
                pinRequestsCopy.Clear();
                lock (_pinRequests)
                {
                    pinned = _pinRequests.Count > 0;
                    if (pinned)
                    {
                        foreach (var pin in _pinRequests)
                            pinRequestsCopy.Add(pin);
                        foreach (var pin in pinRequestsCopy)
                            pin.TrySetResult(true);
                    }
                }

                if (!pinned)
                {
                    var request = Request;
                    if (request.HasValue && HandleRequest(request.Value))
                    {
                        Active = request.Value.State;
                        _request = null;
                    }

                    await KeepInActiveState(stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        private bool HandleRequest(LifecycleStateRequest request)
        {
            var timeUntilActivation = request.ActivateAtUtc - DateTime.UtcNow;
            if (timeUntilActivation <= TimeSpan.Zero)
                return true;
            if (!Countdown.TryGetLastMessageForRemainingTime(timeUntilActivation, out var requestMessage))
                return false;
            if (requestMessage == _lastSentRequestMessage) return false;
            var fullMessage = FormatMessage(in request.State, requestMessage);

            var statusChangeChannel = _runtimeConfig.Current.StatusChangeChannel;
            if (!string.IsNullOrEmpty(statusChangeChannel) && fullMessage != null)
            {
                _sendChatMessagePublisher.SendGenericMessage(statusChangeChannel, fullMessage);
            }

            _lastSentRequestMessage = requestMessage;
            return false;
        }

        private static string FormatMessage(in LifecycleState request, string duration)
        {
            var suffix = string.IsNullOrEmpty(request.Reason) ? "" : $" for {request.Reason}";
            switch (request.State)
            {
                case LifecycleStateCase.Running:
                case LifecycleStateCase.Faulted:
                    return null;
                case LifecycleStateCase.Shutdown:
                    return $"Shutting down in {duration}{suffix}";
                case LifecycleStateCase.Restarting:
                    return $"Restarting in {duration}{suffix}";
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private async Task KeepInActiveState(CancellationToken stoppingToken)
        {
            var desiredState = Active.State;
            switch (desiredState)
            {
                case LifecycleStateCase.Running:
                case LifecycleStateCase.Restarting:
                {
                    var frozen = false;
                    if (desiredState == LifecycleStateCase.Restarting || NeedsRestart(out frozen))
                    {
                        if (frozen)
                        {
                            _log.ZLogError("Taking a core dump from a frozen process");
                            await _diagnostics.CaptureCoreDump(DateTime.UtcNow, "frozen", stoppingToken);
                        }

                        await Stop(stoppingToken);
                        if (stoppingToken.IsCancellationRequested)
                            break;
                        try
                        {
                            await _updater.Update(stoppingToken);
                        }
                        catch (Exception err)
                        {
                            _log.ZLogWarning(err, "Failed to update game binaries. Attempting to continue anyways.");
                        }

                        if (stoppingToken.IsCancellationRequested)
                            break;
                        var result = await Start(stoppingToken);
                        if (result)
                        {
                            if (Active.State == LifecycleStateCase.Restarting)
                                Active = new LifecycleState(LifecycleStateCase.Running);
                        }
                        else
                        {
                            Active = new LifecycleState(LifecycleStateCase.Faulted);
                        }
                    }

                    break;
                }
                case LifecycleStateCase.Faulted:
                case LifecycleStateCase.Shutdown:
                {
                    if (_healthTracker.IsRunning)
                        await Stop(stoppingToken);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private bool NeedsRestart(out bool frozen)
        {
            frozen = false;
            var isRunning = _healthTracker.IsRunning;
            if (isRunning && _startedAt == null)
            {
                _startedAt = DateTime.UtcNow;
                _log.ZLogInformation("Found existing server process {0}, recovering it.", _healthTracker.ActiveProcess.Id);
            }

            if (!_startedAt.HasValue)
                return true;
            var uptime = DateTime.UtcNow - _startedAt.Value;
            if (!isRunning)
            {
                _log.ZLogError("Server has been up for {0:g} and the process disappeared.  Restarting", uptime);
                _healthTracker.Reset();
                Active = new LifecycleState(LifecycleStateCase.Restarting, "Crashed");
                StartStop?.Invoke(StartStopEvent.Crashed, uptime);
                return true;
            }

            if (!_healthTracker.Liveness.State && _healthTracker.Liveness.TimeInState.TotalSeconds > _runtimeConfig.Current.LivenessTimeout)
            {
                _log.ZLogError(
                    "Server has been up for {0:g} and has not been not life for {1:g}.  Restarting",
                    uptime,
                    _healthTracker.Liveness.TimeInState);
                _healthTracker.Reset();
                Active = new LifecycleState(LifecycleStateCase.Restarting, "Crashed");
                StartStop?.Invoke(StartStopEvent.Crashed, uptime);
                return true;
            }

            if ((!_healthTracker.Liveness.IsCurrent || !_healthTracker.Readiness.IsCurrent) && uptime.Ticks > HealthTracker.HealthTimeout.Ticks * 2)
            {
                _log.ZLogError("Server has been up for {0:g} and has stopped reporting.  Restarting", uptime);
                frozen = true;
                _healthTracker.Reset();
                Active = new LifecycleState(LifecycleStateCase.Restarting, "Frozen");
                StartStop?.Invoke(StartStopEvent.Froze, uptime);
                return true;
            }

            if (!_healthTracker.Readiness.State && _healthTracker.Readiness.TimeInState.TotalSeconds > _runtimeConfig.Current.ReadinessTimeout)
            {
                _log.ZLogError(
                    "Server has been up for {0:g} and has not been ready for {1:g}.  Restarting",
                    uptime,
                    _healthTracker.Readiness.TimeInState);
                frozen = true;
                _healthTracker.Reset();
                Active = new LifecycleState(LifecycleStateCase.Restarting, "Frozen");
                StartStop?.Invoke(StartStopEvent.Froze, uptime);
                return true;
            }

            return false;
        }

        private async Task Stop(CancellationToken cancellationToken)
        {
            var process = _healthTracker.ActiveProcess;
            if (process == null)
            {
                _startedAt = null;
                return;
            }

            StartStop?.Invoke(StartStopEvent.Stopping, TimeSpan.Zero);
            if (!process.HasExited)
            {
                _log.ZLogInformation("Requesting shutdown of {0}", process.Id);
                using var builder = _shutdownPublisher.Publish();
                var request = ShutdownRequest.CreateShutdownRequest(builder.Builder, process.Id);
                builder.Send(request);
            }

            var start = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                process = _healthTracker.ActiveProcess;
                if (process == null)
                    break;

                if ((DateTime.UtcNow - start).TotalSeconds > _runtimeConfig.Current.ShutdownTimeout)
                {
                    var killWait = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                    _log.ZLogError("Server has not shutdown in {0:g}.  Killing it", _runtimeConfig.Current.ShutdownTimeout);
                    process.Kill();
                    while (DateTime.UtcNow < killWait && !cancellationToken.IsCancellationRequested)
                    {
                        process = _healthTracker.ActiveProcess;
                        if (process == null)
                            break;
                        await Task.Delay(PollInterval, cancellationToken);
                    }

                    break;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            StartStop?.Invoke(StartStopEvent.Stopped, DateTime.UtcNow - start);
            _startedAt = null;
        }

        private async Task<bool> Start(CancellationToken cancellationToken)
        {
            var process = _healthTracker.ActiveProcess;
            if (process is { HasExited: false })
            {
                _log.ZLogError("Can't start when process {0} {1} is already running", process.Id, process.ProcessName);
                return false;
            }

            _healthTracker.Reset();
            var cfg = _runtimeConfig.Current;
            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = _installConfig.InstallDirectory,
                Arguments = $"\"{_configRenderer.InstallConfigFile}\" \"{_configRenderer.RuntimeConfigFile}\"",
                FileName = Path.Combine(_installConfig.InstallDirectory, cfg.WrapperEntryPoint)
            };
            if (!File.Exists(startInfo.FileName))
            {
                _log.ZLogError("Entry point missing {0}", startInfo.FileName);
                return false;
            }
            var started = Process.Start(startInfo);
            if (started == null)
            {
                _log.ZLogError("Failed to start process {0} {1}", startInfo.FileName, startInfo.Arguments);
                return false;
            }

            _log.ZLogInformation("Started process {0}: {1} {2}", started?.Id, startInfo.FileName, startInfo.Arguments);
            var reportedProcess = _healthTracker.ActiveProcess;
            if (started.Id != reportedProcess?.Id)
                throw new Exception($"Started process {started?.Id} is not the reported process {reportedProcess?.Id}");
            // Wait for process to startup and liveness report
            var start = DateTime.UtcNow;
            StartStop?.Invoke(StartStopEvent.Starting, TimeSpan.Zero);
            _startedAt = start;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_healthTracker.IsRunning)
                {
                    _log.ZLogError("Server exited before reporting liveness");
                    return false;
                }

                var uptime = DateTime.UtcNow - start;

                if (_healthTracker.Liveness.State && _healthTracker.Readiness.State)
                {
                    StartStop?.Invoke(StartStopEvent.Started, uptime);
                    _log.ZLogInformation("Server came up after {0:g}", uptime);
                    return true;
                }

                if (!_healthTracker.Liveness.State && uptime.TotalSeconds > _runtimeConfig.Current.LivenessTimeout)
                {
                    _log.ZLogError("Server did not become alive within {0} seconds", _runtimeConfig.Current.LivenessTimeout);
                    return false;
                }


                if (!_healthTracker.Readiness.State && uptime.TotalSeconds > _runtimeConfig.Current.ReadinessTimeout)
                {
                    _log.ZLogError("Server did not become ready within {0} seconds", _runtimeConfig.Current.ReadinessTimeout);
                    return false;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            return false;
        }

        public PinStateToken PinState() => new PinStateToken(this);

        public readonly struct PinStateToken : IDisposable
        {
            private readonly LifecycleController _controller;
            private readonly TaskCompletionSource<bool> _task;

            internal PinStateToken(LifecycleController ctl)
            {
                _controller = ctl;
                _task = new TaskCompletionSource<bool>();
                var task = _task;
                lock (ctl._pinRequests)
                    ctl._pinRequests.Add(task);
            }

            public Task Task => _task.Task;

            public void Dispose()
            {
                var task = _task;
                lock (_controller._pinRequests)
                    _controller._pinRequests.Remove(task);
            }
        }
    }

    public readonly struct LifecycleStateRequest
    {
        public readonly DateTime ActivateAtUtc;
        public readonly LifecycleState State;

        public LifecycleStateRequest(DateTime activateAtUtc, LifecycleState state)
        {
            ActivateAtUtc = activateAtUtc;
            State = state;
        }
    }

    public readonly struct LifecycleState : IEquatable<LifecycleState>
    {
        public readonly LifecycleStateCase State;
        public readonly string Icon;
        public readonly string Reason;

        public LifecycleState(LifecycleStateCase state, string reason = null, string icon = null)
        {
            State = state;
            Reason = reason;
            Icon = icon;
        }

        public bool Equals(LifecycleState other) => State == other.State && Icon == other.Icon && Reason == other.Reason;

        public override bool Equals(object obj) => obj is LifecycleState other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (int)State;
                hashCode = (hashCode * 397) ^ (Icon != null ? Icon.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Reason != null ? Reason.GetHashCode() : 0);
                return hashCode;
            }
        }
    }

    public class LifecycleStateSerialized : IEquatable<LifecycleStateSerialized>
    {
        [XmlAttribute]
        public LifecycleStateCase State;

        [XmlAttribute]
        public string Icon;

        [XmlAttribute]
        public string Reason;

        public static implicit operator LifecycleState(LifecycleStateSerialized ser) => new LifecycleState(ser.State, ser.Icon, ser.Reason);

        public static implicit operator LifecycleStateSerialized(LifecycleState state) => new LifecycleStateSerialized
        {
            State = state.State,
            Icon = state.Icon,
            Reason = state.Reason
        };

        public bool Equals(LifecycleStateSerialized other) => ((LifecycleState)this).Equals(other);

        public override bool Equals(object obj) => obj is LifecycleStateSerialized ser && Equals(ser);

        public override int GetHashCode() => ((LifecycleState)this).GetHashCode();
    }

    public enum LifecycleStateCase
    {
        Faulted,
        Running,
        Shutdown,
        Restarting,
    }
}