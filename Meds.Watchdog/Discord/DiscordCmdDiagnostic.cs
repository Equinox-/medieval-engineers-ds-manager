using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Watchdog.Save;
using Meds.Watchdog.Utils;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

namespace Meds.Watchdog.Discord
{
    // Not using command groups to allow for discrete permissions.
    public class DiscordCmdDiagnostic : DiscordCmdBase
    {
        private readonly DiagnosticController _diagnostic;
        private readonly ILogger<DiscordCmdDiagnostic> _log;
        private readonly RtcFileSharing _rtcFileSharing;

        public DiscordCmdDiagnostic(DiagnosticController diagnostic, ILogger<DiscordCmdDiagnostic> log, DiscordService discord,
            RtcFileSharing rtcFileSharing) : base(discord)
        {
            _diagnostic = diagnostic;
            _log = log;
            _rtcFileSharing = rtcFileSharing;
        }

        [SlashCommand("diagnostic-core-dump", "Takes a core dump of the server, capturing a full snapshot of its state")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task CoreDumpCommand(
            InteractionContext context,
            [Choice("now", "0")] [Choice("1 minute", "1m")] [Choice("5 minutes", "5m")] [Option("delay", "Delay before core dump, optional.")]
            TimeSpan? delay = default,
            [Option("reason", "Reason for creating the core dump, optional.")]
            string reason = null)
        {
            var at = DateTime.UtcNow + (delay ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(10);
            await context.CreateResponseAsync($"Will try to capture a core dump {at.AsDiscordTime(DiscordTimeFormat.Relative)}");
            var info = await _diagnostic.CaptureCoreDump(at, reason);
            if (info != null)
            {
                await context.EditResponseAsync($"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} core dump {info}");
            }
            else
                await context.EditResponseAsync("Failed to capture a core dump");
        }

        [SlashCommand("diagnostic-profile", "Takes a performance profile of the server.")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task PerformanceProfileCommand(
            InteractionContext context,
            [Choice("1 minute", "1m")] [Choice("5 minutes", "5m")] [Option("duration", "Duration to profile for")]
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
                    $"Captured a {DiscordUtils.FormatHumanBytes(info.Value.Size)} performance profile {info}");
            }
            else
                await context.EditResponseAsync("Failed to capture a performance profile");
        }

        [SlashCommand("diagnostic-download", "Downloads a diagnostic file from the server to the local machine.")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task ShareDownload(InteractionContext context,
            [Option("diagnostic", "Diagnostic file name")] [Autocomplete(typeof(DiagnosticFilesAutoCompleter))]
            string diagnosticName)
        {
            if (!_rtcFileSharing.Enabled)
            {
                await context.CreateResponseAsync("Diagnostic file sharing is not enabled");
                return;
            }

            await context.CreateResponseAsync($"Loading diagnostic `{diagnosticName}`...");
            if (!_diagnostic.TryGetDiagnostic(diagnosticName, out var diagnostic))
            {
                await context.EditResponseAsync($"Failed to load diagnostic `{diagnostic}`");
                return;
            }

            if (!diagnostic.IsZip)
            {
                await context.EditResponseAsync($"Zipping diagnostic `{diagnostic}`");
                diagnostic = _diagnostic.Zipped(diagnostic);
            }

            if (diagnostic.Info.IsDirectory())
            {
                await context.EditResponseAsync($"Diagnostic `{diagnostic}` is a folder, not a file");
                return;
            }

            try
            {
                var progress = new ProgressReporter(context, "Downloaded", "KiB");
                await _rtcFileSharing.Offer(
                    diagnostic.Info.FullName,
                    async (uri, size) => await context.EditResponseAsync($"Will download `{diagnostic}` ({size / 1024.0} KiB) at {uri}"),
                    (name, bytes, totalBytes) =>
                    {
                        progress.Reporter((int)(totalBytes / 1024), (int)(bytes / 1024), 0);
                        return default;
                    });
                await context.EditResponseAsync("No longer available for download");
            }
            catch (RtcTimedOutException)
            {
                await context.EditResponseAsync("Transfer timed out");
            }
        }

        [SlashCommand("diagnostic-inspect", "Inspects a core dump diagnostic file remotely.")]
        [SlashCommandPermissions(DiscordService.CommandPermission)]
        public async Task InspectCoreDump(
            InteractionContext context,
            [Option("diagnostic", "Core dump name")] [Autocomplete(typeof(CoreDumpAutoCompleter))]
            string diagnosticName,
            [Option("include-threads", "List thread stack traces")]
            bool threads = false,
            [Option("include-heap-histogram", "List histogram of objects and their size")]
            bool heapHistogram = false)
        {
            await context.CreateResponseAsync($"Loading diagnostic `{diagnosticName}`...");
            if (!_diagnostic.TryGetDiagnostic(diagnosticName, out var diagnostic))
            {
                await context.EditResponseAsync($"Failed to load diagnostic `{diagnostic}`");
                return;
            }

            using var target = DataTarget.LoadDump(diagnostic.Info.FullName);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            var lines = new List<string>();
            if (threads)
            {
                await context.EditResponseAsync($"Enumerating threads for `{diagnosticName}`...");
                lines.Add("Threads");
                foreach (var thread in runtime.Threads)
                {
                    lines.AddRange(DescribeThread(thread));
                    lines.Add("");
                }
                lines.Add("");
            }

            if (heapHistogram)
            {
                var progress = new ProgressReporter(context, $"Enumerating heap for `{diagnosticName}`", " segments");
                var heap = runtime.Heap;
                lines.Add("Heap Histogram");
                lines.AddRange(DescribeHeap(heap, progress.Reporter));
                await progress.Finish();
                lines.Add("");
            }

            await context.RespondLongText(lines);
        }

        private static IEnumerable<string> DescribeHeap(ClrHeap heap, DelReportProgress reporter)
        {
            if (!heap.CanWalkHeap)
                yield return "  Warning: Heap is not in a consistent state";
            var segments = heap.Segments;
            if (segments.Length == 0)
            {
                yield return "  Heap was not collected";
                yield break;
            }

            var entries = new Dictionary<ClrType, HeapStats>();
            for (var i = 0; i < segments.Length; i++)
            {
                reporter(segments.Length, i, 0);
                foreach (var obj in segments[i].EnumerateObjects())
                {
                    var type = obj.Type;
                    if (type == null || type.IsFree) continue;

                    if (!entries.TryGetValue(type, out var stats))
                        entries.Add(type, stats = new HeapStats());

                    stats.Instances++;
                    stats.Bytes += obj.Size;
                }
            }
            reporter(segments.Length, segments.Length, 0);

            const int limit = 128;
            var table = new DiscordUtils.TableFormatter(0);
            table.AddRow("KiB", "Instances", "Type");
            foreach (var top in entries
                         .OrderByDescending(x => x.Value.Bytes)
                         .Take(limit))
                table.AddRow((top.Value.Bytes / 1024).ToString(), top.Value.Instances.ToString(), top.Key.Name ?? "unknown");
            foreach (var line in table.Lines())
                yield return $"  {line}";
        }

        private class HeapStats
        {
            public ulong Instances;
            public ulong Bytes;
        }

        private static IEnumerable<string> DescribeThread(ClrThread thread)
        {
            yield return $"  Thread id=0x{thread.ManagedThreadId}, osId=0x{thread.OSThreadId:X}, state={thread.State}";
            foreach (var frame in thread.EnumerateStackTrace(false, 32))
                yield return $"    {frame}";
        }
    }
}