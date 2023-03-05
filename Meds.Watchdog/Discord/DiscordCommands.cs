using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Utils;

namespace Meds.Watchdog.Discord
{
    public class DiscordCommands : BaseCommandModule
    {
        private readonly LifetimeController _lifetimeController;
        private readonly HealthTracker _healthTracker;
        private readonly ISubscriber<PlayersResponse> _playersSubscriber;
        private readonly IPublisher<PlayersRequest> _playersRequest;
        private readonly ISubscriber<SaveResponse> _saveResponse;
        private readonly IPublisher<SaveRequest> _saveRequest;
        private readonly DiagnosticController _diagnostic;
        private readonly Configuration _configuration;

        public DiscordCommands(LifetimeController lifetimeController, ISubscriber<PlayersResponse> playersSubscriber, IPublisher<PlayersRequest> playersRequest,
            HealthTracker healthTracker, DiagnosticController diagnostic, ISubscriber<SaveResponse> saveResponse, IPublisher<SaveRequest> saveRequest,
            Configuration configuration)
        {
            _lifetimeController = lifetimeController;
            _playersSubscriber = playersSubscriber;
            _playersRequest = playersRequest;
            _healthTracker = healthTracker;
            _diagnostic = diagnostic;
            _saveResponse = saveResponse;
            _saveRequest = saveRequest;
            _configuration = configuration;
        }

        #region Lifecycle Control

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

        #endregion

        #region Saving

        [Command("save")]
        [Description("Saves the server's world file")]
        [RequirePermission(DiscordPermission.Write)]
        public async Task SaveCommand(CommandContext context,
            [Description("Named backup to take, optional.")]
            string name = null)
        {
            if (!_healthTracker.Readiness.State)
            {
                await context.RespondAsync("Cannot save when server is not running");
                return;
            }

            string backupName = null;
            string backupPath = null;
            if (!string.IsNullOrEmpty(name))
            {
                backupName = $"{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}_{PathUtils.CleanFileName(name)}.zip";
                backupPath = Path.Combine(_configuration.NamedBackupsDirectory, backupName);
            }

            var message = await context.RespondAsync(backupName != null ? $"Saving and backing up to {backupName}..." : "Saving...");
            var start = DateTime.UtcNow;
            var result = await _saveResponse.AwaitResponse(
                response => response.Result,
                TimeSpan.FromMinutes(15),
                () =>
                {
                    using var t = _saveRequest.Publish();
                    t.Send(SaveRequest.CreateSaveRequest(t.Builder, Stopwatch.GetTimestamp(), t.Builder.CreateString(backupPath)));
                });
            var duration = DateTime.UtcNow - start;
            switch (result)
            {
                case SaveResult.Success:
                    if (backupName == null)
                    {
                        await message.ModifyAsync($"Saved in {duration.FormatHumanDuration()}");
                        return;
                    }

                    var backupSize = new System.IO.FileInfo(backupPath).Length;
                    await message.ModifyAsync(
                        $"Saved and backed up to {backupName} ({DiscordUtils.FormatHumanBytes(backupSize)}) in {duration.FormatHumanDuration()}");
                    break;
                case SaveResult.Failed:
                    await message.ModifyAsync("Failed to save");
                    break;
                case SaveResult.TimedOut:
                    await message.ModifyAsync("Timed out when saving");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        #endregion

        #region Status

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

        #endregion

        #region Diagnostics

        [Command("coreDump")]
        [Description("Takes a core dump of the server, capturing a full snapshot of its state.")]
        [RequirePermission(DiscordPermission.Admin)]
        public async Task CoreDumpCommand(
            CommandContext context,
            [Description("Delay before core dump, optional.")]
            TimeSpan delay = default,
            [Description("Reason for creating the core dump, optional.")]
            string reason = null)
        {
            var at = DateTime.UtcNow + delay + TimeSpan.FromMilliseconds(10);
            var message = await context.RespondAsync($"Will try to capture a core dump {at.AsDiscordTime(DiscordTimeFormat.Relative)}");
            var info = await _diagnostic.CaptureCoreDump(at, reason);
            if (info != null)
            {
                await message.ModifyAsync($"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} core dump " +
                                          $"named {Path.GetFileName(info.Value.Path)}");
            }
            else
                await message.ModifyAsync("Failed to capture a core dump");
        }

        [Command("profile")]
        [Description("Takes a performance profile of the server.")]
        [RequirePermission(DiscordPermission.Admin)]
        public async Task PerformanceProfileCommand(
            CommandContext context,
            [Description("Duration to profile for")]
            TimeSpan? duration = default,
            [Description("Profiling mode (sampling [default], or timeline)")]
            DiagnosticController.ProfilingMode mode = DiagnosticController.ProfilingMode.Sampling,
            [Description("Reason for creating the profile, optional.")]
            string reason = null)
        {
            var durationReal = duration ?? TimeSpan.FromMinutes(1);
            var message = await context.RespondAsync("Starting profiling...");
            var info = await _diagnostic.CaptureProfiling(durationReal,
                mode,
                reason,
                starting: () => message.ModifyAsync(
                    $"Capturing performance profile, ends {(DateTime.UtcNow + durationReal).AsDiscordTime(DiscordTimeFormat.Relative)}"));
            if (info != null)
            {
                await message.ModifyAsync(
                    $"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} performance profile " +
                    $"named {Path.GetFileName(info.Value.Path)}");
            }
            else
                await message.ModifyAsync("Failed to capture a performance profile");
        }

        #endregion
    }
}