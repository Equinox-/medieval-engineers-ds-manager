using System;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meds.Watchdog.Discord
{
    public class DiscordService : IHostedService, IDisposable
    {
        private readonly DiscordConfig _config;
        private readonly CommandsNextExtension _commandsNext;

        public DiscordService(Configuration config, ILoggerFactory loggerFactory, IServiceProvider provider)
        {
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
                LoggerFactory = loggerFactory,
            });
            _commandsNext = Client.UseCommandsNext(new CommandsNextConfiguration
            {
                EnableDms = true,
                Services = provider,
            });
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