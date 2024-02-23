using System;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Meds.Shared;

namespace Meds.Watchdog.Discord
{
    // Not using command groups for discrete permissions
    public class DiscordCmdStatus : DiscordCmdBase
    {
        private readonly LifecycleController _lifetimeController;
        private readonly HealthTracker _healthTracker;

        public DiscordCmdStatus(LifecycleController lifetimeController, HealthTracker healthTracker, DiscordService discord) : base(discord)
        {
            _lifetimeController = lifetimeController;
            _healthTracker = healthTracker;
        }

        [SlashCommand("status", "Gets server status, restart schedule, and manual tasks.")]
        [SlashCommandPermissions(Permissions.UseApplicationCommands)]
        public async Task StatusCommand(InteractionContext context)
        {
            var ready = _healthTracker.Readiness.State;
            var players = _healthTracker.PlayerCount;
            var requested = _lifetimeController.Request;
            var currentState = DiscordStatusMonitor.FormatStateRequest(_lifetimeController.Active, ready);
            var builder = new DiscordEmbedBuilder
            {
                Title = currentState
            };
            if (ready)
            {
                builder.AddField("Came Up", _healthTracker.Readiness.ChangedAt.AsDiscordTime(), true);
                var nextDowntime = "None Scheduled";
                if (requested != null)
                {
                    switch (requested.Value.State.State)
                    {
                        case LifecycleStateCase.Shutdown:
                        case LifecycleStateCase.Restarting:
                        {
                            nextDowntime = requested.Value.ActivateAtUtc.AsDiscordTime();
                            break;
                        }
                        case LifecycleStateCase.Faulted:
                        case LifecycleStateCase.Running:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                builder.AddField("Next Downtime", nextDowntime);
                builder.AddField("Online", $"{players} player{(players != 1 ? "s" : "")}", true);
                builder.AddField("Sim Speed", $"{_healthTracker.SimulationSpeed:F02}", true);
            }
            else
            {
                builder.AddField("Went Down", _healthTracker.Readiness.ChangedAt.AsDiscordTime(), true);
                var nextUptime = "None Scheduled";
                if (requested != null)
                {
                    switch (requested.Value.State.State)
                    {
                        case LifecycleStateCase.Shutdown:
                        case LifecycleStateCase.Faulted:
                            break;
                        case LifecycleStateCase.Restarting:
                        case LifecycleStateCase.Running:
                            nextUptime = requested.Value.ActivateAtUtc.AsDiscordTime();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                builder.AddField("Next Uptime", nextUptime, true);
            }

            bool IsGitHash(string hash)
            {
                foreach (var c in hash)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        return false;
                return true;
            }

            string FormatVersion(string gitHash, DateTime compiledAt)
            {
                string hashFmt;
                if (IsGitHash(gitHash))
                {
                    var shortHash = gitHash.Substring(0, Math.Min(gitHash.Length, 8));
                    hashFmt = $"[{shortHash}]({DiscordUtils.RepositoryUrl}/commit/{gitHash})";
                }
                else
                {
                    hashFmt = gitHash;
                }

                return $"{hashFmt} @ {compiledAt.AsDiscordTime()}";
            }

            var watchdogVersion = typeof(DiscordCmdStatus).Assembly.GetCustomAttribute<VersionInfoAttribute>();
            if (watchdogVersion != null)
                builder.AddField("Watchdog Version", FormatVersion(watchdogVersion.GitHash, watchdogVersion.CompiledAt));
            var wrapperVersion = _healthTracker.Version;
            if (wrapperVersion?.GitHash != null)
                builder.AddField("Wrapper Version", FormatVersion(wrapperVersion.Value.GitHash, wrapperVersion.Value.CompiledAtUtc));
            if (wrapperVersion?.Medieval != null)
                builder.AddField("Medieval Version", wrapperVersion.Value.Medieval);

            await context.CreateResponseAsync(builder.Build());
        }
    }
}