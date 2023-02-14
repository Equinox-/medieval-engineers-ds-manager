using System;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using Meds.Shared;
using Meds.Shared.Data;

namespace Meds.Watchdog.Discord
{
    public class DiscordCommands : BaseCommandModule
    {
        private readonly LifetimeController _lifetimeController;
        private readonly HealthTracker _healthTracker;
        private readonly ISubscriber<PlayersResponse> _playersSubscriber;
        private readonly IPublisher<PlayersRequest> _playersRequest;

        public DiscordCommands(LifetimeController lifetimeController, ISubscriber<PlayersResponse> playersSubscriber, IPublisher<PlayersRequest> playersRequest,
            HealthTracker healthTracker)
        {
            _lifetimeController = lifetimeController;
            _playersSubscriber = playersSubscriber;
            _playersRequest = playersRequest;
            _healthTracker = healthTracker;
        }

        [Command("restart")]
        [Description("Restarts the server")]
        [RequirePermission(DiscordPermission.Admin)]
        public Task RestartCommand(CommandContext context,
            [Description("Delay before restart, optional.")]
            TimeSpan delay = default,
            [Description("Reason the server needs to be restarted, optional.")] [RemainingText]
            string reason = null)
        {
            return ChangeState(context, new LifetimeState(LifetimeStateCase.Restarting, reason), delay);
        }

        [Command("shutdown")]
        [Description("Stops the server and keeps it stopped.")]
        [RequirePermission(DiscordPermission.Admin)]
        public Task ShutdownCommand(CommandContext context,
            [Description("Delay before shutdown, optional.")]
            TimeSpan delay = default,
            [Description("Reason the server will be shutdown, optional.")] [RemainingText]
            string reason = null)
        {
            return ChangeState(context, new LifetimeState(LifetimeStateCase.Shutdown, reason), delay);
        }

        [Command("start")]
        [Description("Starts the server.")]
        [RequirePermission(DiscordPermission.Admin)]
        public Task StartCommand(CommandContext context)
        {
            return ChangeState(context, new LifetimeState(LifetimeStateCase.Running), TimeSpan.Zero);
        }

        private async Task ChangeState(CommandContext context, LifetimeState request, TimeSpan delay)
        {
            var prev = _lifetimeController.Active;
            _lifetimeController.Request = new LifetimeStateRequest(DateTime.UtcNow + delay, request);
            var prevState = DiscordStatusMonitor.FormatStateRequest(prev, _healthTracker.Readiness.State);
            var newState = DiscordStatusMonitor.FormatStateRequest(request);
            var delayString = delay > TimeSpan.FromSeconds(1) ? $"in {delay:g}" : "now";
            await context.RespondAsync($"Changing from \"{prevState}\" to \"{newState}\" {delayString}");
        }


        [Command("status")]
        [Description("Gets server status, restart schedule, and manual tasks.")]
        [RequirePermission(DiscordPermission.Read)]
        public async Task StatusCommand(CommandContext context)
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
                        case LifetimeStateCase.Shutdown:
                        case LifetimeStateCase.Restarting:
                        {
                            nextDowntime = requested.Value.ActivateAtUtc.AsDiscordTime();
                            break;
                        }
                        case LifetimeStateCase.Faulted:
                        case LifetimeStateCase.Running:
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
                        case LifetimeStateCase.Shutdown:
                        case LifetimeStateCase.Faulted:
                            break;
                        case LifetimeStateCase.Restarting:
                        case LifetimeStateCase.Running:
                            nextUptime = requested.Value.ActivateAtUtc.AsDiscordTime();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                builder.AddField("Next Uptime", nextUptime);
            }

            builder.WithFooter("ME DS Manager");

            await context.RespondAsync(builder.Build());
        }


        [Command("players")]
        [Description("Lists online players")]
        [RequirePermission(DiscordPermission.Read)]
        public async Task PlayersCommand(CommandContext context)
        {
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
                await context.RespondAsync(response);
            }
            catch (TimeoutException)
            {
                await context.RespondAsync("Server did not respond to players request.  Is it offline?");
            }
        }
    }
}