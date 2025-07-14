using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern DelUnhandledExceptionFilter SetUnhandledExceptionFilter(DelUnhandledExceptionFilter filter);

        private delegate long DelUnhandledExceptionFilter(IntPtr exceptionInfo);

        private static DelUnhandledExceptionFilter _prevExceptionFilter;

        private static DelUnhandledExceptionFilter CoreDumpOnException(string diagnosticsDir) => info =>
        {
            var process = Process.GetCurrentProcess();
            var name = $"core_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}_crash{MinidumpUtils.CoreDumpExt}";
            MinidumpUtils.CaptureAtomic(process, diagnosticsDir, name);
            return _prevExceptionFilter?.Invoke(info) ?? 0;
        };

        public static void Main(string[] args)
        {
            // fail critical errors & no fault error box
            SetErrorMode(3);

            var culture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;

            if (args.Length != 2)
                throw new Exception("Wrapper should not be invoked manually.  [installConfig] [runtimeConfig]");
            var cfg = new Configuration(args[0], args[1]);

            // write core dumps to the diagnostics directory on crash
            // _prevExceptionFilter = SetUnhandledExceptionFilter(CoreDumpOnException(cfg.Install.DiagnosticsDirectory));

            Console.Title = $"[{cfg.Install.Instance}] Server";
            PatchHelper.PatchStartup(cfg.Install);

            using (var instance = new HostBuilder().ConfigureServices(services =>
                       {
                           services.AddSingleton(cfg);
                           services.AddSingleton(cfg.Install);
                           services.AddSingleton<Refreshable<RenderedRuntimeConfig>>(cfg.Runtime);
                           services.AddHostedService(svc => cfg.Runtime.Refreshing(svc));
                           services.AddSingleton<ShimLog>();
                           services.AddSingleton<HealthReporter>();
                           services.AddSingleton<TieredBackups>();
                           services.AddHostedAlias<HealthReporter>();
                           services.AddHostedService<ServerService>();
                           services.AddMedsMessagePipe(cfg.Install.Messaging.WatchdogToServer, cfg.Install.Messaging.ServerToWatchdog);
                           services.AddSingleton<MedsCoreSystemArgs>();
                           services.AddSingletonAndHost<PlayerSystem>();
                           services.AddSingletonAndHost<ChatBridge>();
                           services.AddSingletonAndHost<SavingSystem>();
                       })
                       .Build(cfg.Runtime.Map(x => x.Logging), cfg.Install.LogDirectory))
            {
                Instance = instance;
                Instance.Run();
                Instance = null;
            }

            // Force exit in case a background thread is frozen.
            Environment.Exit(0);
        }

        public static void OnCorruptedState()
        {
            // Give workers a chance to exit.
            Workers.Manager?.WaitAll(TimeSpan.FromMinutes(2));

            // Force shutdown instance.
            try
            {
                Instance?.StopAsync(TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            }
            catch
            {
                // ignore
            }

            // Wait a bit for logging to finish.
            Thread.Sleep(1000);

            // Force dispose instance.
            try
            {
                Instance?.Dispose();
            }
            catch
            {
                // ignore
            }

            // Wait a bit more for random other cleanup.
            Thread.Sleep(1000);

            // Force exit in case a background thread is frozen.
            Environment.Exit(0);
        }
    }
}