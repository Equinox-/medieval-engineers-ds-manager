using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Discord
{
    // Not using permission groups since they can't have discrete permissions
    public class DiscordCmdLifecycle : DiscordCmdBase
    {
        private readonly LifecycleController _lifetimeController;
        private readonly HealthTracker _healthTracker;
        private readonly InstallConfiguration _configuration;
        private readonly ILogger<DiscordCmdLifecycle> _log;
        private readonly IHostApplicationLifetime _lifetime;

        public DiscordCmdLifecycle(LifecycleController lifetimeController, HealthTracker healthTracker,
            InstallConfiguration configuration, ILogger<DiscordCmdLifecycle> log, IHostApplicationLifetime lifetime, DiscordService discord) : base(discord)
        {
            _lifetimeController = lifetimeController;
            _healthTracker = healthTracker;
            _configuration = configuration;
            _log = log;
            _lifetime = lifetime;
        }

        [SlashCommand("lifecycle-restart", "Restarts and updates the server")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public Task RestartCommand(InteractionContext context,
            [Choice("now", "0")]
            [Choice("1 minute", "1m")]
            [Choice("5 minutes", "5m")]
            [Option("delay", "Delay before restart, optional.")]
            TimeSpan? delay = default,
            [Option("reason", "Reason the server needs to be restarted, optional.")]
            string reason = null)
        {
            return ChangeState(context, new LifecycleState(LifecycleStateCase.Restarting, reason), delay);
        }

        [SlashCommand("lifecycle-restart-watchdog", "Restarts and updates the watchdog")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task RestartWatchdogCommand(InteractionContext context)
        {
            var bootstrapPath = _configuration.BootstrapEntryPoint;
            if (!File.Exists(bootstrapPath))
            {
                await context.CreateResponseAsync("Bootstrap binary is missing, watchdog can't be safely restarted");
                return;
            }

            var configFile = _configuration.ConfigFile;
            if (!File.Exists(configFile))
            {
                await context.CreateResponseAsync("Configuration file is missing, watchdog can't be safely restarted");
                return;
            }

            try
            {
                Configuration.Read(configFile);
            }
            catch (Exception err)
            {
                _log.ZLogError(err, "Invalid config file");
                await context.CreateResponseAsync("Configuration file is invalid, watchdog can't be safely restarted");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = bootstrapPath,
                WorkingDirectory = _configuration.Directory,
                Arguments = $"\"{configFile}\" {Process.GetCurrentProcess().Id} true"
            };
            _log.ZLogInformation("Launching bootstrap: {0} {1}", psi.FileName, psi.Arguments);
            await context.CreateResponseAsync("Launching bootstrap...");
            var process = Process.Start(psi);
            if (process == null)
            {
                await context.EditResponseAsync("Failed to launch bootstrap");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            if (process.HasExited)
            {
                _log.ZLogError("Bootstrap exited without waiting for watchdog");
                await context.EditResponseAsync("Bootstrap exited prematurely");
                return;
            }

            await context.EditResponseAsync("Watchdog restarting");
            _lifetime.StopApplication();
        }

        [SlashCommand("lifecycle-shutdown", "Stops the server and keeps it stopped.")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public Task ShutdownCommand(InteractionContext context,
            [Choice("now", "0")]
            [Choice("1 minute", "1m")]
            [Choice("5 minutes", "5m")]
            [Option("delay", "Delay before shutdown, optional.")]
            TimeSpan? delay = default,
            [Option("reason", "Reason the server will be shutdown, optional.")]
            string reason = null)
        {
            return ChangeState(context, new LifecycleState(LifecycleStateCase.Shutdown, reason), delay);
        }

        [SlashCommand("lifecycle-start", "Starts the server.")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public Task StartCommand(InteractionContext context)
        {
            return ChangeState(context, new LifecycleState(LifecycleStateCase.Running));
        }

        private async Task ChangeState(InteractionContext context, LifecycleState request, TimeSpan? delay = null)
        {
            var realDelay = delay ?? TimeSpan.Zero;
            var prev = _lifetimeController.Active;
            _lifetimeController.Request = new LifecycleStateRequest(DateTime.UtcNow + realDelay , request);
            var prevState = DiscordStatusMonitor.FormatStateRequest(prev, _healthTracker.Readiness.State);
            var newState = DiscordStatusMonitor.FormatStateRequest(request);
            var delayString = delay > TimeSpan.FromSeconds(1) ? $"in {realDelay:g}" : "now";
            await context.CreateResponseAsync($"Changing from \"{prevState}\" to \"{newState}\" {delayString}");
        }
    }
}