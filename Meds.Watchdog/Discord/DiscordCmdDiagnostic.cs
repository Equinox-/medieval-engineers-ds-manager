using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.Discord
{
    public class DiscordCmdDiagnostic : BaseCommandModule
    {
        private readonly DiagnosticController _diagnostic;
        private readonly ILogger<DiscordCmdDiagnostic> _log;

        public DiscordCmdDiagnostic(DiagnosticController diagnostic, ILogger<DiscordCmdDiagnostic> log)
        {
            _diagnostic = diagnostic;
            _log = log;
        }

        [Command("coreDump")]
        [Description("Takes a core dump of the server, capturing a full snapshot of its state.")]
        [RequirePermission(DiscordPermission.DiagnosticsCoreDump)]
        public async Task CoreDumpCommand(
            CommandContext context,
            [Description("Delay before core dump, optional.")]
            TimeSpan delay = default,
            [Description("Reason for creating the core dump, optional.")]
            string reason = null)
        {
            var at = DateTime.UtcNow + delay + TimeSpan.FromMilliseconds(10);
            var message = await context.RespondAsync($"Will try to capture a core dump {at.AsDiscordTime(DiscordTimeFormat.Relative)}");
            var info = await _diagnostic.CaptureCoreDump(at, reason);
            if (info != null)
            {
                await message.ModifyAsync($"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} core dump " +
                                          $"named {Path.GetFileName(info.Value.Path)}");
            }
            else
                await message.ModifyAsync("Failed to capture a core dump");
        }

        [Command("profile")]
        [Description("Takes a performance profile of the server.")]
        [RequirePermission(DiscordPermission.DiagnosticsProfile)]
        public async Task PerformanceProfileCommand(
            CommandContext context,
            [Description("Duration to profile for")]
            TimeSpan? duration = default,
            [Description("Profiling mode (sampling [default], or timeline)")]
            DiagnosticController.ProfilingMode mode = DiagnosticController.ProfilingMode.Sampling,
            [Description("Reason for creating the profile, optional.")]
            string reason = null)
        {
            var durationReal = duration ?? TimeSpan.FromMinutes(1);
            var message = await context.RespondAsync("Starting profiling...");
            var info = await _diagnostic.CaptureProfiling(durationReal,
                mode,
                reason,
                starting: () => message.ModifyAsync(
                    $"Capturing performance profile, ends {(DateTime.UtcNow + durationReal).AsDiscordTime(DiscordTimeFormat.Relative)}"));
            if (info != null)
            {
                await message.ModifyAsync(
                    $"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} performance profile " +
                    $"named {Path.GetFileName(info.Value.Path)}");
            }
            else
                await message.ModifyAsync("Failed to capture a performance profile");
        }
    }
}