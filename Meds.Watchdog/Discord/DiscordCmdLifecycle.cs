using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Discord
{
    public class DiscordCmdLifecycle : BaseCommandModule
    {
        private readonly LifetimeController _lifetimeController;
        private readonly HealthTracker _healthTracker;
        private readonly Configuration _configuration;
        private readonly ILogger<DiscordCmdLifecycle> _log;
        private readonly IHostApplicationLifetime _lifetime;

        public DiscordCmdLifecycle(LifetimeController lifetimeController, HealthTracker healthTracker,
            Configuration configuration, ILogger<DiscordCmdLifecycle> log, IHostApplicationLifetime lifetime)
        {
            _lifetimeController = lifetimeController;
            _healthTracker = healthTracker;
            _configuration = configuration;
            _log = log;
            _lifetime = lifetime;
        }

        [Command("restart")]
        [Description("Restarts and updates the server")]
        [RequirePermission(DiscordPermission.ServerLifecycleRestart)]
        public Task RestartCommand(CommandContext context,
            [Description("Delay before restart, optional.")]
            TimeSpan delay = default,
            [Description("Reason the server needs to be restarted, optional.")] [RemainingText]
            string reason = null)
        {
            return ChangeState(context, new LifetimeState(LifetimeStateCase.Restarting, reason), delay);
        }

        [Command("restart-watchdog")]
        [Description("Restarts and updates the watchdog")]
        [RequirePermission(DiscordPermission.WatchdogRestart)]
        public async Task RestartWatchdogCommand(CommandContext context)
        {
            var bootstrapPath = _configuration.BootstrapEntryPoint;
            if (!File.Exists(bootstrapPath))
            {
                await context.RespondAsync("Bootstrap binary is missing, watchdog can't be safely restarted");
                return;
            }

            var configFile = _configuration.ConfigFile;
            if (!File.Exists(configFile))
            {
                await context.RespondAsync("Configuration file is missing, watchdog can't be safely restarted");
                return;
            }

            try
            {
                Configuration.Read(configFile);
            }
            catch (Exception err)
            {
                _log.ZLogError(err, "Invalid config file");
                await context.RespondAsync("Configuration file is invalid, watchdog can't be safely restarted");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = bootstrapPath,
                WorkingDirectory = _configuration.Directory,
                Arguments = $"\"{configFile}\" {Process.GetCurrentProcess().Id} true"
            };
            _log.ZLogInformation("Launching bootstrap: {0} {1}", psi.FileName, psi.Arguments);
            var process = Process.Start(psi);
            if (process == null)
            {
                await context.RespondAsync("Failed to launch bootstrap");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            if (process.HasExited)
            {
                _log.ZLogError("Bootstrap exited without waiting for watchdog");
                await context.RespondAsync("Bootstrap exited prematurely");
                return;
            }

            await context.RespondAsync("Watchdog restarting");
            _lifetime.StopApplication();
        }

        [Command("shutdown")]
        [Description("Stops the server and keeps it stopped.")]
        [RequirePermission(DiscordPermission.ServerLifecycleStop)]
        public Task ShutdownCommand(CommandContext context,
            [Description("Delay before shutdown, optional.")]
            TimeSpan delay = default,
            [Description("Reason the server will be shutdown, optional.")] [RemainingText]
            string reason = null)
        {
            return ChangeState(context, new LifetimeState(LifetimeStateCase.Shutdown, reason), delay);
        }

        [Command("start")]
        [Description("Starts the server.")]
        [RequirePermission(DiscordPermission.ServerLifecycleStart)]
        public Task StartCommand(CommandContext context)
        {
            return ChangeState(context, new LifetimeState(LifetimeStateCase.Running), TimeSpan.Zero);
        }

        private async Task ChangeState(CommandContext context, LifetimeState request, TimeSpan delay)
        {
            var prev = _lifetimeController.Active;
            _lifetimeController.Request = new LifetimeStateRequest(DateTime.UtcNow + delay, request);
            var prevState = DiscordStatusMonitor.FormatStateRequest(prev, _healthTracker.Readiness.State);
            var newState = DiscordStatusMonitor.FormatStateRequest(request);
            var delayString = delay > TimeSpan.FromSeconds(1) ? $"in {delay:g}" : "now";
            await context.RespondAsync($"Changing from \"{prevState}\" to \"{newState}\" {delayString}");
        }
    }
}