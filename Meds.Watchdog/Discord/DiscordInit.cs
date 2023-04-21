using System;
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
        private readonly Refreshable<DiscordConfig> _config;
        private readonly ILoggerFactory _rootLogger;
        private readonly IServiceProvider _services;
        private readonly ILogger<DiscordService> _log;

        public DiscordClient Client => _state?.Client;

        public DiscordService(Refreshable<Configuration> config, ILoggerFactory rootLogger, IServiceProvider provider, ILogger<DiscordService> log)
        {
            _config = config.Map(x => x.Discord);
            _rootLogger = rootLogger;
            _services = provider;
            _log = log;
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
                    AckPaginationButtons = true,
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
                    args.Context.FollowUpAsync(new DiscordFollowupMessageBuilder { Content = $"Command processing failed!  Error ID = {uuid}" });
                    return Task.CompletedTask;
                };

                commands.RegisterCommands<DiscordCmdDiagnostic>();
                commands.RegisterCommands<DiscordCmdLifecycle>();
                commands.RegisterCommands<DiscordCmdSave>();
                commands.RegisterCommands<DiscordCmdSaveSearch>();
                commands.RegisterCommands<DiscordCmdStatus>();
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


        private IDisposable _clientConnector;

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
    }
}