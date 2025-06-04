using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus.SlashCommands;
using JetBrains.Profiler.SelfApi;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Discord;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class DiagnosticController
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private const string TempSuffix = ".tmp";

        private readonly IPublisher<ChatMessage> _chatPublisher;
        private readonly Refreshable<Configuration> _runtimeConfig;
        private readonly InstallConfiguration _installConfig;
        private readonly HealthTracker _healthTracker;
        private readonly ILogger<DiagnosticController> _log;

        public DiagnosticController(IPublisher<ChatMessage> chatPublisher,
            InstallConfiguration installConfig, Refreshable<Configuration> runtimeConfig,
            HealthTracker healthTracker, ILogger<DiagnosticController> log)
        {
            _chatPublisher = chatPublisher;
            _runtimeConfig = runtimeConfig;
            _installConfig = installConfig;
            _healthTracker = healthTracker;
            _log = log;
        }

        public async Task<DiagnosticOutput?> CaptureCoreDump(DateTime atUtc, string reason = null, CancellationToken cancellationToken = default)
        {
            string prevMessage = null;
            Process process;
            while (true)
            {
                process = _healthTracker.ActiveProcess;
                if (process == null || process.HasExited)
                    return null;
                var remaining = atUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;
                if (cancellationToken.IsCancellationRequested)
                {
                    _chatPublisher.SendGenericMessage(
                        _runtimeConfig.Current.StatusChangeChannel,
                        "Core dump canceled");
                    return null;
                }

                if (Countdown.TryGetLastMessageForRemainingTime(remaining, out var newMessage) && newMessage != prevMessage)
                {
                    _chatPublisher.SendGenericMessage(
                        _runtimeConfig.Current.StatusChangeChannel,
                        $"Capturing core dump in {newMessage}");
                    prevMessage = newMessage;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            var name = $"core_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}{CleanAndPrefix(reason)}{MinidumpUtils.CoreDumpExt}";
            var dir = _installConfig.DiagnosticsDirectory;
            try
            {
                var finalPath = MinidumpUtils.CaptureAtomic(process, dir, name);
                if (finalPath == null)
                {
                    _log.ZLogWarning("Failed to collect minidump");
                    return null;
                }

                _autocompletes.Clear();
                return new DiagnosticOutput(new FileInfo(finalPath));
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to collect minidump");
                return null;
            }
        }

        public enum ProfilingMode
        {
            [ChoiceName("Sampling")]
            Sampling,

            [ChoiceName("Timeline")]
            Timeline,
        }

        public async Task<DiagnosticOutput?> CaptureProfiling(
            TimeSpan duration,
            ProfilingMode mode,
            string reason = null,
            CancellationToken cancellationToken = default,
            Func<Task> starting = null)
        {
            var process = _healthTracker.ActiveProcess;
            if (process == null || process.HasExited)
                return null;
            var name = $"prof_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}{CleanAndPrefix(reason)}";
            var finalDir = Path.Combine(_installConfig.DiagnosticsDirectory, name);
            var tempDir = Path.Combine(_installConfig.DiagnosticsDirectory, name + TempSuffix);
            Directory.CreateDirectory(tempDir);
            try
            {
                var rootProfilePath = Path.Combine(tempDir, "prof.dtp");
                await DotTrace.InitAsync(cancellationToken);
                var runnerField = typeof(DotTrace).GetField("ConsoleRunnerPackage", BindingFlags.Static | BindingFlags.NonPublic);
                var runner = runnerField?.GetValue(null);
                if (runner == null)
                {
                    _log.ZLogWarning("Failed to download dotTrace CLI");
                    return null;
                }

                var runnerPath = runner.GetType()
                    .GetMethod("GetRunnerPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(runner, Array.Empty<object>())?.ToString();
                if (runnerPath == null || !File.Exists(runnerPath))
                {
                    _log.ZLogWarning("Failed to download dotTrace CLI");
                    return null;
                }

                string modeArgs;
                switch (mode)
                {
                    case ProfilingMode.Sampling:
                        modeArgs = "--profiling-type=Sampling";
                        break;
                    case ProfilingMode.Timeline:
                        modeArgs = "--profiling-type=Timeline";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                }

                var profiler = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = runnerPath,
                        Arguments =
                            $"attach {process.Id} {modeArgs} --collect-data-from-start=On \"--save-to={rootProfilePath}\" --timeout={duration.TotalSeconds}s",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    }
                };
                if (!profiler.Start())
                    return null;

                _chatPublisher.SendGenericMessage(_runtimeConfig.Current.StatusChangeChannel, $"Profiling for {duration.FormatHumanDuration()}");
                await (starting?.Invoke() ?? Task.CompletedTask);

                while (!profiler.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;
                    await Task.Delay(PollInterval, cancellationToken);
                }

                _chatPublisher.SendGenericMessage(_runtimeConfig.Current.StatusChangeChannel, "Done profiling");

                if (!File.Exists(rootProfilePath))
                {
                    _log.ZLogWarning("Profiler output file missing");
                    return null;
                }

                Directory.Move(tempDir, finalDir);
                _autocompletes.Clear();
                return new DiagnosticOutput(new FileInfo(finalDir));
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to collect profiling data");
                return null;
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        private static string CleanAndPrefix(string value) => string.IsNullOrWhiteSpace(value) ? "" : ("_" + PathUtils.CleanFileName(value));

        #region Diagnostic List

        public IEnumerable<DiagnosticOutput> AllDiagnostics
        {
            get
            {
                var dir = new DirectoryInfo(_installConfig.DiagnosticsDirectory);
                if (!dir.Exists)
                    return Enumerable.Empty<DiagnosticOutput>();
                return dir.GetFiles()
                    .Where(info => !info.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .Select(info => new DiagnosticOutput(info))
                    .Where(info => info.Size > 0);
            }
        }

        private readonly struct CachedAutocomplete : IEquatable<CachedAutocomplete>
        {
            public readonly AutoCompleteTree<DiagnosticOutput> Tree;
            public readonly long ExpiresAt;

            public CachedAutocomplete(AutoCompleteTree<DiagnosticOutput> tree)
            {
                Tree = tree;
                ExpiresAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
            }

            public bool Expired => Stopwatch.GetTimestamp() >= ExpiresAt;

            public bool Equals(CachedAutocomplete other) => Tree.Equals(other.Tree);

            public override bool Equals(object obj) => obj is CachedAutocomplete other && Equals(other);

            public override int GetHashCode() => Tree.GetHashCode();
        }

        private readonly ConcurrentDictionary<string, CachedAutocomplete> _autocompletes = new ConcurrentDictionary<string, CachedAutocomplete>();

        private CachedAutocomplete CreateAutocomplete(string ext) => new CachedAutocomplete(new AutoCompleteTree<DiagnosticOutput>(AllDiagnostics
            .Where(x => x.Info.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Info.Name, x))));

        public IEnumerable<AutoCompleteTree<DiagnosticOutput>.Result> AutoCompleteDiagnostic(string prompt, int? limit = null, string ext = "")
        {
            return _autocompletes.AddOrUpdate(ext, CreateAutocomplete,
                    (extCaptured, existing) => existing.Expired ? CreateAutocomplete(extCaptured) : existing)
                .Tree
                .Apply(prompt, limit);
        }

        public bool TryGetDiagnostic(string name, out DiagnosticOutput diagnostic)
        {
            diagnostic = default;
            if (name.StartsWith("/") || name.StartsWith("\\") || name.Contains("..") || name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                return false;
            diagnostic = new DiagnosticOutput(new FileInfo(Path.Combine(_installConfig.DiagnosticsDirectory, name)));
            return diagnostic.Size > 0;
        }

        private readonly object _zipDiagnosticLock = new object();

        public DiagnosticOutput Zipped(DiagnosticOutput diagnostic)
        {
            if (diagnostic.IsZip)
                return diagnostic;
            var zippedName = diagnostic.Info.Name + ".zip";
            var finalZipPath = Path.Combine(_installConfig.DiagnosticsDirectory, zippedName);
            lock (_zipDiagnosticLock)
            {
                if (TryGetDiagnostic(zippedName, out var zipped))
                    return zipped;
                var tempZipPath = Path.Combine(_installConfig.DiagnosticsDirectory, zippedName + TempSuffix);
                try
                {
                    using (var zipFile = new ZipArchive(new FileStream(tempZipPath, FileMode.CreateNew), ZipArchiveMode.Create))
                    {
                        var dir = diagnostic.Directory;
                        if (dir != null)
                        {
                            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
                                if (!file.IsDirectory())
                                    Add(file);
                        }
                        else
                        {
                            Add(diagnostic.Info);
                        }

                        void Add(FileInfo file)
                        {
                            var fullPath = file.FullName;
                            if (!fullPath.StartsWith(_installConfig.DiagnosticsDirectory, StringComparison.OrdinalIgnoreCase))
                                return;
                            var relPath = fullPath.Substring(_installConfig.DiagnosticsDirectory.Length).TrimStart('/', '\\');
                            var entry = zipFile.CreateEntry(relPath);
                            using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
                            using var target = entry.Open();
                            source.CopyTo(target);
                        }
                    }

                    File.Move(tempZipPath, finalZipPath);
                    if (diagnostic.Info.IsDirectory())
                        Directory.Delete(diagnostic.Info.FullName, true);
                    else
                        File.Delete(diagnostic.Info.FullName);
                    _autocompletes.Clear();
                    return new DiagnosticOutput(new FileInfo(finalZipPath));
                }
                finally
                {
                    File.Delete(tempZipPath);
                }
            }
        }

        #endregion
    }

    public readonly struct DiagnosticOutput
    {
        public readonly FileInfo Info;
        public readonly long Size;

        public DirectoryInfo Directory => Info.IsDirectory() ? new DirectoryInfo(Info.FullName) : null;

        public bool IsZip => Info.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        public DiagnosticOutput(FileInfo info)
        {
            Info = info;
            if (!info.Exists)
            {
                Size = 0;
                return;
            }

            if (!info.IsDirectory())
            {
                Size = info.Length;
                return;
            }

            var dir = new DirectoryInfo(info.FullName);
            Size = 0;
            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
                if (!file.IsDirectory())
                    Size += file.Length;
        }

        public override string ToString() => Info.Name;
    }

    public class DiagnosticFilesAutoCompleter : DiscordAutoCompleter<DiagnosticOutput>
    {
        protected virtual string Extension => "";

        protected sealed override IEnumerable<AutoCompleteTree<DiagnosticOutput>.Result> Provide(AutocompleteContext ctx, string prefix)
        {
            return ctx.Services.GetRequiredService<DiagnosticController>().AutoCompleteDiagnostic(prefix, ext: Extension);
        }

        protected sealed override string FormatData(string key, DiagnosticOutput data) => key;

        protected sealed override string FormatArgument(DiagnosticOutput data) => data.Info.Name;
    }

    public sealed class CoreDumpAutoCompleter : DiagnosticFilesAutoCompleter
    {
        protected override string Extension => MinidumpUtils.CoreDumpExt;
    }
}