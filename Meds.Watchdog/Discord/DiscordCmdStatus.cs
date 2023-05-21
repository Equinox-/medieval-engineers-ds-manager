using System;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Watchdog.Discord
{
    // Not using command groups for discrete permissions
    public class DiscordCmdStatus : ApplicationCommandModule
    {
        private readonly LifecycleController _lifetimeController;
        private readonly HealthTracker _healthTracker;
        private readonly ISubscriber<PlayersResponse> _playersSubscriber;
        private readonly IPublisher<PlayersRequest> _playersRequest;

        public DiscordCmdStatus(LifecycleController lifetimeController, ISubscriber<PlayersResponse> playersSubscriber,
            IPublisher<PlayersRequest> playersRequest, HealthTracker healthTracker)
        {
            _lifetimeController = lifetimeController;
            _playersSubscriber = playersSubscriber;
            _playersRequest = playersRequest;
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

            string FormatVersion(string gitHash, DateTime compiledAt)
            {
                var shortHash = gitHash.Substring(0, Math.Min(gitHash.Length, 8));
                return $"[{shortHash}]({DiscordUtils.RepositoryUrl}/commit/{gitHash}) @ {compiledAt.AsDiscordTime()}";
            }

            var watchdogVersionInfo = typeof(DiscordCmdStatus).Assembly.GetCustomAttribute<VersionInfoAttribute>();
            if (watchdogVersionInfo != null)
                builder.AddField("Watchdog Version", FormatVersion(watchdogVersionInfo.GitHash, watchdogVersionInfo.CompiledAt));
            if (_healthTracker.VersionHash != null)
                builder.AddField("Wrapper Version", FormatVersion(_healthTracker.VersionHash, _healthTracker.VersionCompiledAt));

            await context.CreateResponseAsync(builder.Build());
        }


        [SlashCommand("players", "Lists online players")]
        [SlashCommandPermissions(Permissions.UseApplicationCommands)]
        public async Task PlayersCommand(InteractionContext context)
        {
            await context.CreateResponseAsync(InteractionResponseType.DeferredChannelMessageWithSource);
            try
            {
                var response = await _playersSubscriber.AwaitResponse(msg =>
                {
                    var response = new StringBuilder();
                    response.Append($"{msg.PlayersLength} Online Players");
                    for (var i = 0; i < msg.PlayersLength; i++)
                    {
                        response.Append("\n");
                        // ReSharper disable once PossibleInvalidOperationException
                        var player = msg.Players(i).Value;
                        if (player.FactionTag != null)
                            response.Append("[").Append(player.FactionTag).Append("] ");
                        response.Append(player.Name);
                        switch (player.Promotion)
                        {
                            case PlayerPromotionLevel.Moderator:
                                response.Append(" (Moderator)");
                                break;
                            case PlayerPromotionLevel.Admin:
                                response.Append(" (Admin)");
                                break;
                            case PlayerPromotionLevel.None:
                            default:
                                break;
                        }
                    }

                    return response.ToString();
                }, sendRequest: () =>
                {
                    using var token = _playersRequest.Publish();
                    PlayersRequest.StartPlayersRequest(token.Builder);
                    token.Send(PlayersRequest.EndPlayersRequest(token.Builder));
                });
                await context.EditResponseAsync(response);
            }
            catch (TimeoutException)
            {
                await context.EditResponseAsync("Server did not respond to players request.  Is it offline?");
            }
        }
    }
}