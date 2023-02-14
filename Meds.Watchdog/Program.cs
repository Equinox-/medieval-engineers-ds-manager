using System;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Watchdog.Discord;
using Meds.Watchdog.Steam;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SteamKit2;

namespace Meds.Watchdog
{
    public sealed class Program
    {
        public static async Task Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "restore-game-binaries")
            {
                await CiUtils.RestoreGameBinaries(args[1], args[2]);
                return;
            }

            if (args.Length < 1)
            {
                await Console.Error.WriteLineAsync("Usage: Meds.Watchdog.exe config.xml");
                return;
            }

            var cfg = Configuration.Read(args[0]);
            using var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(cfg);
                    services.AddSingleton(typeof(Program));
                    services.AddSteamDownloader(SteamConfiguration.Create(x => { }));
                    services.AddMedsMessagePipe(cfg.Messaging.ServerToWatchdog, cfg.Messaging.WatchdogToServer);
                    services.AddSingleton<Updater>();
                    services.AddSingleton<ConfigRenderer>();
                    services.AddSingletonAndHost<HealthTracker>();
                    services.AddSingletonAndHost<LifetimeController>();
                    services.AddSingleton<DiscordPermissionController>();
                    services.AddSingletonAndHost<DiscordService>();
                    services.AddSingletonAndHost<DiscordStatusMonitor>();
                    services.AddSingletonAndHost<DiscordMessageBridge>();
                })
                .Build();
            await host.RunAsync();
        }
    }
}