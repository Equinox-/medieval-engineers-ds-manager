using System;
using Meds.Shared;
using Meds.Wrapper.Shim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meds.Wrapper
{
    public class Entrypoint
    {
        public static IHost Instance { get; private set; }

        public static Configuration Config => Instance.Services.GetRequiredService<Configuration>();

        public static ILogger LoggerFor(Type type) => Instance.Services.GetRequiredService<ILoggerFactory>().CreateLogger(type);

        public static void Main(string[] args)
        {
            if (args.Length != 1)
                throw new Exception("Wrapper should not be invoked manually.  [installConfig]");
            var cfg = new Configuration(args[0]);

            PatchHelper.PatchStartup(cfg.Install.Adjustments.ReplaceLogger ?? false);

            using var instance = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<Configuration>(cfg);
                    services.AddSingleton<ShimLog>();
                    services.AddSingleton<HealthReporter>();
                    services.AddHostedAlias<HealthReporter>();
                    services.AddHostedService<ServerService>();
                    services.AddMedsMessagePipe(cfg.Install.Messaging.WatchdogToServer, cfg.Install.Messaging.ServerToWatchdog);
                    services.AddSingleton<MedsCoreSystemArgs>();
                    services.AddSingletonAndHost<PlayerReporter>();
                    services.AddSingletonAndHost<ChatBridge>();
                    services.AddSingletonAndHost<SavingSystem>();
                })
                .Build(cfg.Install.LogDirectory);
            Instance = instance;
            Instance.Run();
            Instance = null;
        }
    }
}