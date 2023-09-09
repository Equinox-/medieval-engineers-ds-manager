using System;
using Meds.Shared;
using Meds.Wrapper.Shim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VRage.ParallelWorkers;

namespace Meds.Wrapper
{
    public static class Entrypoint
    {
        public static IHost Instance { get; private set; }

        public static Configuration Config => Instance.Services.GetRequiredService<Configuration>();

        public static ILogger LoggerFor(Type type) => Instance?.Services.GetRequiredService<ILoggerFactory>().CreateLogger(type);

        public static void Main(string[] args)
        {
            if (args.Length != 2)
                throw new Exception("Wrapper should not be invoked manually.  [installConfig] [runtimeConfig]");
            var cfg = new Configuration(args[0], args[1]);

            PatchHelper.PatchStartup(cfg.Install.Adjustments.ReplaceLogger ?? false);

            using var instance = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(cfg);
                    services.AddSingleton(cfg.Install);
                    services.AddSingleton<Refreshable<RenderedRuntimeConfig>>(cfg.Runtime);
                    services.AddHostedService(svc => cfg.Runtime.Refreshing(svc));
                    services.AddSingleton<ShimLog>();
                    services.AddSingleton<HealthReporter>();
                    services.AddHostedAlias<HealthReporter>();
                    services.AddHostedService<ServerService>();
                    services.AddMedsMessagePipe(cfg.Install.Messaging.WatchdogToServer, cfg.Install.Messaging.ServerToWatchdog);
                    services.AddSingleton<MedsCoreSystemArgs>();
                    services.AddSingletonAndHost<PlayerSystem>();
                    services.AddSingletonAndHost<ChatBridge>();
                    services.AddSingletonAndHost<SavingSystem>();
                })
                .Build(cfg.Install.LogDirectory);
            Instance = instance;
            Instance.Run();
            Instance = null;
            // Give workers a chance to exit.
            Workers.Manager?.WaitAll(TimeSpan.FromMinutes(2));
            // Force exit in case a background thread is frozen.
            Environment.Exit(0);
        }
    }
}