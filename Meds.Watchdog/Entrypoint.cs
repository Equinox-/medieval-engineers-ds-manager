using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Meds.Dist;
using Meds.Shared;
using Meds.Watchdog.Discord;
using Meds.Watchdog.GrafanaAgent;
using Meds.Watchdog.Save;
using Meds.Watchdog.Steam;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SteamKit2;

namespace Meds.Watchdog
{
    public sealed class Entrypoint
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        public static async Task Main(string[] args)
        {
            // fail critical errors & no fault error box
            SetErrorMode(3);

            var culture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;

            if (args.Length == 3 && args[0] == "restore-game-binaries")
            {
                await CiUtils.RestoreGameBinaries(args[1], args[2]);
                return;
            }

            var configFile = args.Length > 0 ? args[0] : DistUtils.DiscoverConfigFile();

            if (configFile == null)
            {
                await Console.Error.WriteLineAsync(
                    "Either provide a configuration file as an argument, or place one in the same folder or parent folder of Meds.Watchdog.exe");
                return;
            }

            var cfg = ConfigRefreshable<Configuration>.FromConfigFile(configFile, Configuration.Read);
            InstallConfiguration installConfig = cfg.Current;
            Console.Title = $"[{installConfig.Instance}] Watchdog";
            var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(cfg);
                    services.AddHostedService(svc => cfg.Refreshing(svc));
                    services.AddSingleton(installConfig);
                    services.AddSingleton<Refreshable<Configuration>>(cfg);
                    services.AddHostedService(svc => cfg.Refreshing(svc));
                    services.AddSingleton(typeof(Entrypoint));
                    services.AddSteamDownloader(SteamConfiguration.Create(x => { }));
                    services.AddMedsMessagePipe(
                        installConfig.Messaging.ServerToWatchdog,
                        installConfig.Messaging.WatchdogToServer);
                    services.AddSingleton<Updater>();
                    services.AddSingletonAndHost<ConfigRenderer>();
                    services.AddSingletonAndHost<HealthTracker>();
                    services.AddSingletonAndHost<LifecycleController>();
                    services.AddSingletonAndHost<DiscordService>();
                    services.AddSingletonAndHost<DiscordStatusMonitor>();
                    services.AddSingletonAndHost<DiscordMessageBridge>();
                    services.AddSingletonAndHost<DataStore>();
                    services.AddSingletonAndHost<GaController>();
                    services.AddSingletonAndHost<LogRetention>();
                    services.AddSingleton(GaConfigRenderer.Create);
                    services.AddSingleton<SaveFiles>();
                    services.AddSingleton<DiagnosticController>();
                    services.AddSingleton<RtcFileSharing>();
                })
                .Build(installConfig.WatchdogLogs);
            using (host)
            {
                await host.RunAsync();
            }

            // Force exit in case a background thread is frozen.
            Environment.Exit(0);
        }
    }
}