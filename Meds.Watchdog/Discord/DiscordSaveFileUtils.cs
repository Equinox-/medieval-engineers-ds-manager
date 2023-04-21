using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using Meds.Watchdog.Save;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Meds.Watchdog.Discord
{
    public abstract class SaveFilesAutoCompleter : DiscordAutoCompleter<SaveFile>
    {
        protected abstract IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(SaveFiles files, string prefix);

        protected override IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(AutocompleteContext ctx, string prefix)
        {
            return Provide(ctx.Services.GetRequiredService<SaveFiles>(), prefix);
        }

        protected override string FormatData(string key, SaveFile data) => key;
    }

    public sealed class AllSaveFilesAutoCompleter : SaveFilesAutoCompleter
    {
        protected override IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(SaveFiles files, string prefix) => files.AutoCompleteSave(prefix);
    }

    public sealed class AutomaticSaveFilesAutoCompleter : SaveFilesAutoCompleter
    {
        protected override IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(SaveFiles files, string prefix) => files.AutoCompleteAutomaticSave(prefix);
    }

    public sealed class ProgressReporter
    {
        private static readonly long ReportInterval = (long)TimeSpan.FromSeconds(5).TotalSeconds * Stopwatch.Frequency;
        private long _lastReporting = Stopwatch.GetTimestamp() + ReportInterval;
        private volatile Task _reportingTask = Task.CompletedTask;

        public Task ReportingTask => _reportingTask;

        public readonly DelReportProgress Reporter;

        public ProgressReporter(InteractionContext ctx)
        {
            Reporter = (total, successful, failed) =>
            {
                var last = Volatile.Read(ref _lastReporting);
                var now = Stopwatch.GetTimestamp();
                if (last + ReportInterval >= now)
                    return;
                if (Interlocked.CompareExchange(ref _lastReporting, now, last) != last)
                    return;
                lock (this)
                {
                    var complete = successful + failed;
                    if (!_reportingTask.IsCompleted) return;
                    _reportingTask = ctx.EditResponseAsync($"Processed {complete * 100 / total:D}% ({complete} / {total})");
                }
            };
        }
    }
}