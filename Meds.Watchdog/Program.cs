using System;
using System.IO;
using System.Reflection;
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

            var configFile = args.Length > 0 ? args[0] : DiscoverConfigFile();

            if (configFile == null)
            {
                await Console.Error.WriteLineAsync("Either provide a configuration file as an argument, or place one in the same folder or parent folder of Meds.Watchdog.exe");
                return;
            }

            var cfg = Configuration.Read(configFile);
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

        private static string DiscoverConfigFile()
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var parent = Path.GetDirectoryName(assembly);
            if (parent == null)
                return null;
            var candidate1 = Path.Combine(parent, "config.xml");
            if (File.Exists(candidate1))
                return candidate1;
            var grandparent = Path.GetDirectoryName(parent);
            if (grandparent == null)
                return null;
            var candidate2 = Path.Combine(grandparent, "config.xml");
            return File.Exists(candidate2) ? candidate2 : null;
        }
    }
}