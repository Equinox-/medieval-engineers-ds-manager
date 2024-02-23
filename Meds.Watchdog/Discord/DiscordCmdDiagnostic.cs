using System;
using System.IO;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.SlashCommands;
using Microsoft.Extensions.Logging;

namespace Meds.Watchdog.Discord
{
    // Not using command groups to allow for discrete permissions.
    public class DiscordCmdDiagnostic : DiscordCmdBase
    {
        private readonly DiagnosticController _diagnostic;
        private readonly ILogger<DiscordCmdDiagnostic> _log;

        public DiscordCmdDiagnostic(DiagnosticController diagnostic, ILogger<DiscordCmdDiagnostic> log, DiscordService discord) : base(discord)
        {
            _diagnostic = diagnostic;
            _log = log;
        }

        [SlashCommand("diagnostic-core-dump", "Takes a core dump of the server, capturing a full snapshot of its state")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task CoreDumpCommand(
            InteractionContext context,
            [Choice("now", "0")]
            [Choice("1 minute", "1m")]
            [Choice("5 minutes", "5m")]
            [Option("delay", "Delay before core dump, optional.")]
            TimeSpan? delay = default,
            [Option("reason", "Reason for creating the core dump, optional.")]
            string reason = null)
        {
            var at = DateTime.UtcNow + (delay ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(10);
            await context.CreateResponseAsync($"Will try to capture a core dump {at.AsDiscordTime(DiscordTimeFormat.Relative)}");
            var info = await _diagnostic.CaptureCoreDump(at, reason);
            if (info != null)
            {
                await context.EditResponseAsync($"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} core dump " +
                                          $"named {Path.GetFileName(info.Value.Path)}");
            }
            else
                await context.EditResponseAsync("Failed to capture a core dump");
        }

        [SlashCommand("diagnostic-profile", "Takes a performance profile of the server.")]
        [SlashCommandPermissions(Permissions.Administrator)]
        public async Task PerformanceProfileCommand(
            InteractionContext context,
            [Choice("1 minute", "1m")]
            [Choice("5 minutes", "5m")]
            [Option("duration", "Duration to profile for")]
            TimeSpan? duration = default,
            [Option("mode", "Profiling mode (sampling [default], or timeline)")]
            DiagnosticController.ProfilingMode mode = DiagnosticController.ProfilingMode.Sampling,
            [Option("reason", "Reason for creating the profile, optional.")]
            string reason = null)
        {
            var durationReal = duration ?? TimeSpan.FromMinutes(1);
            await context.CreateResponseAsync("Starting profiling...");
            var info = await _diagnostic.CaptureProfiling(durationReal,
                mode,
                reason,
                starting: () => context.EditResponseAsync(
                    $"Capturing performance profile, ends {(DateTime.UtcNow + durationReal).AsDiscordTime(DiscordTimeFormat.Relative)}"));
            if (info != null)
            {
                await context.EditResponseAsync(
                    $"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} performance profile " +
                    $"named {Path.GetFileName(info.Value.Path)}");
            }
            else
                await context.EditResponseAsync("Failed to capture a performance profile");
        }
    }
}