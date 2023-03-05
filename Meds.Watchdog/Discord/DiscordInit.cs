using System;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Converters;
using Meds.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;
using Serilog.Extensions.Logging;

namespace Meds.Watchdog.Discord
{
    public class DiscordService : IHostedService, IDisposable
    {
        private readonly DiscordConfig _config;
        private readonly CommandsNextExtension _commandsNext;
        private readonly ILogger<DiscordService> _log;

        public DiscordService(Configuration config, ILoggerFactory rootLogger, IServiceProvider provider, ILogger<DiscordService> log)
        {
            _log = log;
            _config = config.Discord;
            if (!Enabled)
            {
                Client = null;
                return;
            }

            Client = new DiscordClient(new DiscordConfiguration
            {
                Token = _config.Token,
                Intents = DiscordIntents.AllUnprivileged,
                AutoReconnect = true,
                LoggerFactory = rootLogger,
            });
            _commandsNext = Client.UseCommandsNext(new CommandsNextConfiguration
            {
                EnableDms = true,
                Services = provider,
            });
            _commandsNext.RegisterConverter(new EnumConverter<DiagnosticController.ProfilingMode>());
            _commandsNext.CommandErrored += (_, args) =>
            {
                var uuid = Guid.NewGuid();
                _log.ZLogWarning(args.Exception, "Command {0} ({1}) failed, {2}",
                    args.Command?.Name, args.Context.Message.Content, uuid);
                args.Context.RespondAsync($"Command processing failed!  Error ID = {uuid}");
                return Task.CompletedTask;
            };
        }

        public bool Enabled => !string.IsNullOrEmpty(_config.Token);

        public DiscordClient Client { get; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Client != null)
            {
                _commandsNext.RegisterCommands<DiscordCommands>();
                await Client.ConnectAsync();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Client != null)
                await Client.DisconnectAsync();
        }

        public void Dispose()
        {
            Client?.Dispose();
        }
    }
}