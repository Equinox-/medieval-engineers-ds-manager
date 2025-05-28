using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Interactivity;
using DSharpPlus.Interactivity.Enums;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Discord
{
    public class DiscordService : IHostedService, IDisposable
    {
        public const Permissions CommandPermission = Permissions.KickMembers;
        private readonly Refreshable<DiscordConfig> _config;
        private readonly ILoggerFactory _rootLogger;
        private readonly IServiceProvider _services;
        private readonly ILogger<DiscordService> _log;
        private readonly Refreshable<HashSet<ulong>> _requiredGuilds;
        private readonly Refreshable<HashSet<ulong>> _requiredChannels;

        private IDisposable _clientConnector;

        public DiscordClient Client => _state?.Client;

        public DiscordService(Refreshable<Configuration> config, ILoggerFactory rootLogger, IServiceProvider provider, ILogger<DiscordService> log)
        {
            _config = config.Map(x => x.Discord);
            _rootLogger = rootLogger;
            _services = provider;
            _log = log;
            _requiredGuilds = _config.Map(x => new HashSet<ulong>(x.RequireGuild ?? new List<ulong>()));
            _requiredChannels = _config.Map(x => new HashSet<ulong>(x.RequireChannel ?? new List<ulong>()));
        }

        private volatile State _state;

        private sealed class State
        {
            internal readonly DiscordClient Client;

            public State(DiscordService owner, string token)
            {
                Client = new DiscordClient(new DiscordConfiguration
                {
                    Intents = DiscordIntents.AllUnprivileged,
                    AutoReconnect = true,
                    LoggerFactory = owner._rootLogger,
                    Token = token,
                });

                Client.UseInteractivity(new InteractivityConfiguration
                {
                    Timeout = TimeSpan.FromMinutes(5),
                    ButtonBehavior = ButtonPaginationBehavior.DeleteButtons,
                    PaginationBehaviour = PaginationBehaviour.Ignore,
                    ResponseBehavior = InteractionResponseBehavior.Ack,
                });

                var commands = Client.UseSlashCommands(new SlashCommandsConfiguration
                {
                    Services = owner._services,
                });
                commands.SlashCommandErrored += (_, args) =>
                {
                    var uuid = Guid.NewGuid();
                    owner._log.ZLogWarning(args.Exception, "Command {0} ({1}) failed, {2}",
                        args.Context.CommandName,
                        string.Join(", ", args.Context.Interaction?.Data?.Options?.Select(x => x.Name + " " + x.Value) ?? Array.Empty<string>()),
                        uuid);
                    return args.Context.FollowUpAsync(new DiscordFollowupMessageBuilder { Content = $"Command processing failed!  Error ID = `{uuid}`, Message = `{args.Exception?.Message}`" });
                };

                commands.RegisterCommands<DiscordCmdDiagnostic>();
                commands.RegisterCommands<DiscordCmdLifecycle>();
                commands.RegisterCommands<DiscordCmdSave>();
                commands.RegisterCommands<DiscordCmdSaveSearch>();
                commands.RegisterCommands<DiscordCmdStatus>();
                commands.RegisterCommands<DiscordCmdPlayers>();
                commands.RegisterCommands<DiscordCmdConfig>();
            }
        }

        private volatile Task _refreshToken;

        private async Task RefreshToken(Task prevTask, string token)
        {
            if (prevTask != null)
                await prevTask;
            var prev = _state;
            if (prev != null)
                await _state.Client.DisconnectAsync();
            if (string.IsNullOrEmpty(token))
            {
                _state = null;
                return;
            }

            _state = new State(this, token);
            await _state.Client.ConnectAsync();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _clientConnector = _config
                .Map(x => x.Token)
                .Subscribe(token => _refreshToken = RefreshToken(_refreshToken, token));
            var initial = _refreshToken;
            if (initial != null)
                await initial;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _clientConnector.Dispose();
            _refreshToken = RefreshToken(_refreshToken, null);
            return _refreshToken;
        }

        public void Dispose()
        {
            Client?.Dispose();
        }

        private bool CheckRequirements(BaseContext ctx)
        {
            var guilds = _requiredGuilds.Current;
            var channels = _requiredChannels.Current;
            if (guilds.Count == 0 && channels.Count == 0)
                return true;
            if (ctx.Guild != null && guilds.Contains(ctx.Guild.Id))
                return true;
            if (channels.Contains(ctx.Channel.Id))
                return true;
            return false;
        }

        internal async Task<bool> VerifyRequirements(BaseContext ctx)
        {
            if (CheckRequirements(ctx)) return true;
            _log.ZLogWarning("Command {0} was used from an invalid location", ctx.CommandName);
            await ctx.CreateResponseAsync("Command used from invalid location");
            return false;
        }
    }
}