using System;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using Microsoft.Extensions.Hosting;

namespace Meds.Watchdog.Discord
{
    public class DiscordStatusMonitor : BackgroundService
    {
        private readonly DiscordService _discord;
        private readonly HealthTracker _tracker;
        private readonly LifecycleController _lifetime;
        private StatusArgs _prevStatus;

        public DiscordStatusMonitor(DiscordService discord, LifecycleController lifetime, HealthTracker tracker)
        {
            _discord = discord;
            _tracker = tracker;
            _lifetime = lifetime;
        }

        public static string FormatStateRequest(LifecycleState request, bool readiness = false)
        {
            switch (request.State)
            {
                case LifecycleStateCase.Running:
                    return $"{request.Icon ?? (readiness ? "🟩" : "⏳")} | {request.Reason ?? (readiness ? "Running" : "Starting")}";
                case LifecycleStateCase.Shutdown:
                    return $"{request.Icon ?? "💤"} | {request.Reason ?? "Shutdown"}";
                case LifecycleStateCase.Restarting:
                    return $"{request.Icon ?? "♻️"} | {request.Reason ?? "Restarting"}";
                case LifecycleStateCase.Faulted:
                    return $"{request.Icon ?? "🪦"} | {request.Reason ?? "Faulted"}";
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Short delay for discord to come up.
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = _discord.Client;
                if (client != null)
                {
                    var status = new StatusArgs
                    {
                        Pid = _tracker.ActiveProcess?.Id ?? 0,
                        RequestedState = _lifetime.Active,
                        Liveness = _tracker.Liveness.State,
                        Readiness = _tracker.Readiness.State,
                        ReadinessChangedAt = _tracker.Readiness.ChangedAt,
                        Players = _tracker.PlayerCount,
                        SimulationSpeed = _tracker.SimulationSpeed,
                    };
                    if (!_prevStatus.Equals(status))
                    {
                        var (activity, userStatus, since) = Render(in status);
                        await client.UpdateStatusAsync(activity, userStatus, since);
                        _prevStatus = status;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        private static (DiscordActivity, UserStatus, DateTime?) Render(in StatusArgs status)
        {
            var request = status.RequestedState;
            switch (request.State)
            {
                case LifecycleStateCase.Running:
                    if (status.Readiness)
                        return (new DiscordActivity($"🚶 {status.Players} | 📈 {status.SimulationSpeed:F2}", ActivityType.Playing), UserStatus.Online, null);
                    return (new DiscordActivity($"{request.Icon ?? "⏳"} | Starting", ActivityType.Watching), UserStatus.Idle, status.ReadinessChangedAt);
                case LifecycleStateCase.Shutdown:
                    return (new DiscordActivity(FormatStateRequest(request), ActivityType.Watching), UserStatus.DoNotDisturb, status.ReadinessChangedAt);
                case LifecycleStateCase.Restarting:
                    return (new DiscordActivity(FormatStateRequest(request), ActivityType.Watching), UserStatus.Idle, status.ReadinessChangedAt);
                case LifecycleStateCase.Faulted:
                    return (new DiscordActivity(FormatStateRequest(request), ActivityType.Watching), UserStatus.DoNotDisturb, status.ReadinessChangedAt);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public  struct StatusArgs : IEquatable<StatusArgs>
        {
            public int Pid;
            public LifecycleState RequestedState;
            public bool Liveness;
            public bool Readiness;
            public DateTime ReadinessChangedAt;
            public int Players;
            public float SimulationSpeed;

            public bool Equals(StatusArgs other)
            {
                return Pid == other.Pid && RequestedState.Equals(other.RequestedState) && Liveness == other.Liveness && Readiness == other.Readiness && ReadinessChangedAt.Equals(other.ReadinessChangedAt) && Players == other.Players && SimulationSpeed.Equals(other.SimulationSpeed);
            }

            public override bool Equals(object obj)
            {
                return obj is StatusArgs other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = Pid;
                    hashCode = (hashCode * 397) ^ RequestedState.GetHashCode();
                    hashCode = (hashCode * 397) ^ Liveness.GetHashCode();
                    hashCode = (hashCode * 397) ^ Readiness.GetHashCode();
                    hashCode = (hashCode * 397) ^ ReadinessChangedAt.GetHashCode();
                    hashCode = (hashCode * 397) ^ Players;
                    hashCode = (hashCode * 397) ^ SimulationSpeed.GetHashCode();
                    return hashCode;
                }
            }
        }
    }
}