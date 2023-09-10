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

        protected override string FormatArgument(SaveFile data) => data.SaveName;
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
        private readonly InteractionContext _ctx;
        private readonly string _prefix;
        private int _total, _successful, _failed;

        private long _lastReporting = Stopwatch.GetTimestamp();
        private volatile Task _reportingTask = Task.CompletedTask;

        public readonly DelReportProgress Reporter;

        private string Message(string prefix = null)
        {
            lock (this)
            {
                var total = _total;
                var complete = _successful + _failed;
                return $"{prefix ?? _prefix} {(total == 0 ? 100 : complete * 100 / total):D}% ({complete} / {total})";
            }
        }

        public ProgressReporter(InteractionContext ctx, string prefix = "Processed")
        {
            _ctx = ctx;
            _prefix = prefix;
            Reporter = (total, successful, failed) =>
            {
                Volatile.Write(ref _total, total);
                Volatile.Write(ref _successful, successful);
                Volatile.Write(ref _failed, failed);
                var last = Volatile.Read(ref _lastReporting);
                var now = Stopwatch.GetTimestamp();
                if (last + ReportInterval >= now)
                    return;
                if (Interlocked.CompareExchange(ref _lastReporting, now, last) != last)
                    return;
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