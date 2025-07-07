using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using Meds.Shared;
using Meds.Watchdog.Save;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Meds.Watchdog.Discord
{
    public abstract class SaveFilesAutoCompleter : DiscordAutoCompleter<SaveFile>
    {
        protected abstract IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(SaveFiles files, string prefix, int limit);

        protected override IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(AutocompleteContext ctx, string prefix)
        {
            var saves = ctx.Services.GetRequiredService<SaveFiles>();
            SaveFile latest = null;
            var hasLatest = SaveFiles.LatestBackup.StartsWith(prefix) && saves.TryOpenLatestSave(out latest);
            if (hasLatest)
                yield return new AutoCompleteTree<SaveFile>.Result(SaveFiles.LatestBackup, 1, latest);
            foreach (var result in Provide(saves, prefix, ResultLimit - (hasLatest ? 1 : 0)))
                yield return result;
        }

        protected override string FormatData(string key, SaveFile data) => $"{data.SaveName} ({data.TimeUtc})";

        protected override string FormatArgument(SaveFile data) => data.SaveName;
    }

    public sealed class AllSaveFilesAutoCompleter : SaveFilesAutoCompleter
    {
        protected override IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(SaveFiles files, string prefix, int limit) => files.AutoCompleteSave(prefix, limit);
    }

    public sealed class AutomaticSaveFilesAutoCompleter : SaveFilesAutoCompleter
    {
        protected override IEnumerable<AutoCompleteTree<SaveFile>.Result> Provide(SaveFiles files, string prefix, int limit) => files.AutoCompleteAutomaticSave(prefix, limit);
    }

    public sealed class ProgressReporter
    {
        private static readonly long ReportInterval = (long)TimeSpan.FromSeconds(5).TotalSeconds * Stopwatch.Frequency;
        private readonly InteractionContext _ctx;
        private readonly string _prefix;
        private readonly string _unit;
        private int _total, _successful, _failed;
        private int _lastDone;

        private long _lastReporting = Stopwatch.GetTimestamp();
        private volatile Task _reportingTask = Task.CompletedTask;

        public readonly DelReportProgress Reporter;

        private string Message(string prefix = null)
        {
            lock (this)
            {
                var total = _total;
                var complete = _successful + _failed;
                return $"{prefix ?? _prefix} {(total == 0 ? 100 : complete * 100 / total):D}% ({complete} / {total}{_unit})";
            }
        }

        public ProgressReporter(InteractionContext ctx, string prefix = "Processed", string unit = "")
        {
            _ctx = ctx;
            _prefix = prefix;
            _unit = string.IsNullOrEmpty(unit) ? "" : " " + unit;
            Reporter = (total, successful, failed) =>
            {
                Volatile.Write(ref _total, total);
                Volatile.Write(ref _successful, successful);
                Volatile.Write(ref _failed, failed);
                var last = Volatile.Read(ref _lastReporting);
                var now = Stopwatch.GetTimestamp();
                var lastDone = Volatile.Read(ref _lastDone);
                var done = successful + failed >= total ? 1 : 0;
                if (last + ReportInterval >= now && done == lastDone) return;

                var timeUpdated = Interlocked.CompareExchange(ref _lastReporting, now, last) == last;
                var doneUpdated = done != lastDone && Interlocked.CompareExchange(ref _lastDone, done, lastDone) == lastDone;
                if (!doneUpdated && !timeUpdated) return;
                lock (this)
                {
                    if (!_reportingTask.IsCompleted) return;
                    _reportingTask = _ctx.EditResponseAsync(Message());
                }
            };
        }

        public async Task Finish(string prefix = null)
        {
            await _reportingTask;
            await _ctx.EditResponseAsync(Message(prefix));
        }
    }
}