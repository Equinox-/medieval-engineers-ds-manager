using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Shared.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;

namespace Meds.Watchdog
{
    public class LifetimeController : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CronTabBuffer = TimeSpan.FromSeconds(30);

        private readonly ILogger<LifetimeController> _log;
        private readonly HealthTracker _healthTracker;
        private readonly Configuration _config;

        private DateTime? _startedAt;
        private readonly IPublisher<ShutdownRequest> _shutdownPublisher;
        private readonly IPublisher<ChatMessage> _sendChatMessagePublisher;
        private readonly Updater _updater;
        private readonly ConfigRenderer _configRenderer;
        private readonly List<(CrontabSchedule schedule, bool utc, LifetimeState target)> _scheduled = new List<(CrontabSchedule, bool, LifetimeState)>();

        public delegate void DelStateChanged(LifetimeState previousState, LifetimeState currentState);

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

        private LifetimeState _active = new LifetimeState(LifetimeStateCase.Running);

        public LifetimeState Active
        {
            get => _active;
            set
            {
                if (value.Equals(_active))
                    return;
                var prev = _active;
                _active = value;
                StateChanged?.Invoke(prev, _active);
            }
        }

        private LifetimeStateRequest? _request;
        private string _lastSentRequestMessage;

        public LifetimeStateRequest? Request
        {
            get
            {
                var best = _request;
                foreach (var (schedule, utc, target) in _scheduled)
                {
                    var nextOccurrenceUtc = utc
                        ? schedule.GetNextOccurrence(DateTime.UtcNow - CronTabBuffer)
                        : schedule.GetNextOccurrence(DateTime.Now - CronTabBuffer).ToUniversalTime();
                    if (best == null || best.Value.ActivateAtUtc > nextOccurrenceUtc)
                        best = new LifetimeStateRequest(nextOccurrenceUtc, target);
                }

                return best;
            }
            set
            {
                _request = value;
                _lastSentRequestMessage = null;
            }
        }

        public LifetimeController(ILogger<LifetimeController> logger,
            HealthTracker health,
            Configuration config,
            IPublisher<ShutdownRequest> shutdownPublisher,
            Updater updater,
            ConfigRenderer configRenderer, IPublisher<ChatMessage> sendChatMessagePublisher)
        {
            _log = logger;
            _healthTracker = health;
            _config = config;
            _updater = updater;
            _shutdownPublisher = shutdownPublisher;
            _configRenderer = configRenderer;
            _sendChatMessagePublisher = sendChatMessagePublisher;

            if (config.ScheduledTasks != null)
                foreach (var task in config.ScheduledTasks)
                {
                    try
                    {
                        var schedule = CrontabSchedule.Parse(task.Cron);
                        _scheduled.Add((schedule, task.Utc, new LifetimeState(task.Target, task.Reason)));
                    }
                    catch (Exception err)
                    {
                        _log.LogWarning(err, "Failed to parse crontab {Cron}", task.Cron);
                    }
                }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_request.HasValue && HandleRequest(_request.Value))
                {
                    Active = _request.Value.State;
                    _request = null;
                }

                await KeepInActiveState(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        private static readonly List<(TimeSpan, string)> MessageForDuration = new List<(TimeSpan, string)>
        {
            (TimeSpan.FromSeconds(5), "5 seconds"),
            (TimeSpan.FromSeconds(10), "10 seconds"),
            (TimeSpan.FromSeconds(15), "15 seconds"),
            (TimeSpan.FromSeconds(30), "30 seconds"),
            (TimeSpan.FromMinutes(1), "1 minute"),
            (TimeSpan.FromMinutes(2), "2 minutes"),
            (TimeSpan.FromMinutes(3), "3 minutes"),
            (TimeSpan.FromMinutes(4), "4 minutes"),
            (TimeSpan.FromMinutes(5), "5 minutes"),
            (TimeSpan.FromMinutes(10), "10 minutes"),
            (TimeSpan.FromMinutes(15), "15 minutes"),
            (TimeSpan.FromMinutes(20), "20 minutes"),
            (TimeSpan.FromMinutes(25), "25 minutes"),
            (TimeSpan.FromMinutes(30), "30 minutes")
        };

        private class MessageForDurationComparer : IComparer<(TimeSpan, string)>
        {
            public static readonly MessageForDurationComparer Instance = new MessageForDurationComparer();

            public int Compare((TimeSpan, string) x, (TimeSpan, string) y) => x.Item1.CompareTo(y.Item1);
        }

        private bool HandleRequest(LifetimeStateRequest request)
        {
            var timeUntilActivation = request.ActivateAtUtc - DateTime.UtcNow;
            if (timeUntilActivation <= TimeSpan.Zero)
                return true;
            var idx = MessageForDuration.BinarySearch((timeUntilActivation, null), MessageForDurationComparer.Instance);
            if (idx < 0)
                idx = ~idx;
            if (idx >= MessageForDuration.Count) return false;
            var requestMessage = MessageForDuration[idx].Item2;
            if (requestMessage == _lastSentRequestMessage) return false;
            var fullMessage = FormatMessage(in request.State, requestMessage);
            if (!string.IsNullOrEmpty(_config.StatusChangeChannel) && fullMessage != null)
            {
                using var token = _sendChatMessagePublisher.Publish();
                var builder = token.Builder;
                token.Send(ChatMessage.CreateChatMessage(builder,
                    ChatChannel.GenericChatChannel,
                    channelOffset: GenericChatChannel.CreateGenericChatChannel(builder, builder.CreateString(_config.StatusChangeChannel)).Value,
                    messageOffset: builder.CreateString(fullMessage)
                ));
            }

            _lastSentRequestMessage = requestMessage;
            return false;
        }

        private static string FormatMessage(in LifetimeState request, string duration)
        {
            var suffix = string.IsNullOrEmpty(request.Reason) ? "" : $" for {request.Reason}";
            switch (request.State)
            {
                case LifetimeStateCase.Running:
                case LifetimeStateCase.Faulted:
                    return null;
                case LifetimeStateCase.Shutdown:
                    return $"Shutting down in {duration}{suffix}";
                case LifetimeStateCase.Restarting:
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
                case LifetimeStateCase.Running:
                case LifetimeStateCase.Restarting:
                {
                    if (desiredState == LifetimeStateCase.Restarting || NeedsRestart())
                    {
                        await Stop(stoppingToken);
                        if (stoppingToken.IsCancellationRequested)
                            break;
                        await _updater.Update(stoppingToken);
                        if (stoppingToken.IsCancellationRequested)
                            break;
                        var result = await Start(stoppingToken);
                        if (result)
                        {
                            if (Active.State == LifetimeStateCase.Restarting)
                                Active = new LifetimeState(LifetimeStateCase.Running);
                        }
                        else
                        {
                            Active = new LifetimeState(LifetimeStateCase.Faulted);
                        }
                    }

                    break;
                }
                case LifetimeStateCase.Faulted:
                case LifetimeStateCase.Shutdown:
                {
                    if (_healthTracker.IsRunning)
                        await Stop(stoppingToken);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            await Stop(cancellationToken);
        }

        private bool NeedsRestart()
        {
            var isRunning = _healthTracker.IsRunning;
            if (isRunning && _startedAt == null)
            {
                _startedAt = DateTime.UtcNow;
                _log.LogInformation($"Found existing server process {_healthTracker.ActiveProcess.Id}, recovering it.");
            }

            if (!_startedAt.HasValue)
                return true;
            var uptime = DateTime.UtcNow - _startedAt.Value;
            if (!isRunning)
            {
                _log.LogError($"Server has been up for {uptime} and the process disappeared.  Restarting");
                _healthTracker.Reset();
                Active = new LifetimeState(LifetimeStateCase.Restarting, "Crashed");
                StartStop?.Invoke(StartStopEvent.Crashed, uptime);
                return true;
            }

            if (!_healthTracker.Liveness.State && _healthTracker.Liveness.TimeInState.TotalSeconds > _config.LivenessTimeout)
            {
                _log.LogError(
                    "Server has been up for {Uptime:g} and has not been not life for {TimeNotLive:g}.  Restarting",
                    uptime,
                    _healthTracker.Liveness.TimeInState);
                _healthTracker.Reset();
                Active = new LifetimeState(LifetimeStateCase.Restarting, "Crashed");
                StartStop?.Invoke(StartStopEvent.Crashed, uptime);
                return true;
            }

            if ((!_healthTracker.Liveness.IsCurrent || !_healthTracker.Readiness.IsCurrent) && uptime.Ticks > HealthTracker.HealthTimeout.Ticks * 2)
            {
                _log.LogError("Server has been up for {Uptime:g} and has stopped reporting.  Restarting", uptime);
                _healthTracker.Reset();
                Active = new LifetimeState(LifetimeStateCase.Restarting, "Frozen");
                StartStop?.Invoke(StartStopEvent.Froze, uptime);
                return true;
            }

            if (!_healthTracker.Readiness.State && _healthTracker.Readiness.TimeInState.TotalSeconds > _config.ReadinessTimeout)
            {
                _log.LogError(
                    "Server has been up for {Uptime:g} and has not been ready for {TimeNotReady:g}.  Restarting",
                    uptime,
                    _healthTracker.Readiness.TimeInState);
                _healthTracker.Reset();
                Active = new LifetimeState(LifetimeStateCase.Restarting, "Frozen");
                StartStop?.Invoke(StartStopEvent.Froze, uptime);
                return true;
            }

            return false;
        }

        private async Task Stop(CancellationToken cancellationToken)
        {
            var process = _healthTracker.ActiveProcess;
            if (process == null || process.HasExited)
            {
                _startedAt = null;
                return;
            }
            StartStop?.Invoke(StartStopEvent.Stopping, TimeSpan.Zero);
            _log.LogInformation("Requesting shutdown of {Process}", process.Id);
            using (var builder = _shutdownPublisher.Publish())
            {
                var request = ShutdownRequest.CreateShutdownRequest(builder.Builder, process.Id);
                builder.Send(request);
            }

            var start = DateTime.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (process.HasExited)
                    break;

                if ((DateTime.UtcNow - start).TotalSeconds > _config.ShutdownTimeout)
                {
                    _log.LogError("Server has not shutdown in {Timeout:g}.  Killing it.", _config.ShutdownTimeout);
                    process.Kill();
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
                throw new Exception($"Can't start when process {process.ProcessName} is already running");

            var renderedConfig = _configRenderer.Render();
            _healthTracker.Reset();
            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = _config.InstallDirectory,
                Arguments = $"\"{renderedConfig.InstallConfigPath}\"",
                FileName = Path.Combine(_config.InstallDirectory, _config.EntryPoint)
            };
            var started = Process.Start(startInfo);
            if (started == null)
            {
                _log.LogError("Failed to start process {Exe} {Args}", startInfo.FileName, startInfo.Arguments);
                return false;
            }

            _log.LogInformation("Started process {Pid}: {Exe} {Args}", started?.Id, startInfo.FileName, startInfo.Arguments);
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
                    _log.LogError("Server exited before reporting liveness");
                    return false;
                }

                var uptime = DateTime.UtcNow - start;

                if (_healthTracker.Liveness.State && _healthTracker.Readiness.State)
                {
                    StartStop?.Invoke(StartStopEvent.Started, uptime);
                    _log.LogInformation("Server came up after {Uptime:g}", uptime);
                    return true;
                }

                if (!_healthTracker.Liveness.State && uptime.TotalSeconds > _config.LivenessTimeout)
                {
                    _log.LogError("Server did not become alive within {Timeout} seconds", _config.LivenessTimeout);
                    return false;
                }


                if (!_healthTracker.Readiness.State && uptime.TotalSeconds > _config.ReadinessTimeout)
                {
                    _log.LogError("Server did not become ready within {Timeout} seconds", _config.ReadinessTimeout);
                    return false;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            return false;
        }
    }

    public readonly struct LifetimeStateRequest
    {
        public readonly DateTime ActivateAtUtc;
        public readonly LifetimeState State;

        public LifetimeStateRequest(DateTime activateAtUtc, LifetimeState state)
        {
            ActivateAtUtc = activateAtUtc;
            State = state;
        }
    }

    public readonly struct LifetimeState : IEquatable<LifetimeState>
    {
        public readonly LifetimeStateCase State;
        public readonly string Icon;
        public readonly string Reason;

        public LifetimeState(LifetimeStateCase state, string reason = null, string icon = null)
        {
            State = state;
            Reason = reason;
            Icon = icon;
        }

        public bool Equals(LifetimeState other) => State == other.State && Icon == other.Icon && Reason == other.Reason;

        public override bool Equals(object obj) => obj is LifetimeState other && Equals(other);

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

    public enum LifetimeStateCase
    {
        Faulted,
        Running,
        Shutdown,
        Restarting,
    }
}