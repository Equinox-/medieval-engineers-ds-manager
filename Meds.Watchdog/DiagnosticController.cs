using System;
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

            var name = $"core_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}{CleanAndPrefix(reason)}.dmp";
            var dir = _installConfig.DiagnosticsDirectory;
            Directory.CreateDirectory(dir);
            var finalPath = Path.Combine(dir, name);
            var tempPath = Path.Combine(dir, name + TempSuffix);
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Write))
                {
                    const MinidumpType type = MinidumpType.WithFullMemory | MinidumpType.WithProcessThreadData
                                                                          | MinidumpType.WithThreadInfo | MinidumpType.WithUnloadedModules;
                    if (!MiniDumpWriteDump(process.Handle, process.Id, stream.SafeFileHandle, type,
                            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    {
                        _log.ZLogWarning("Failed to collect minidump");
                        return null;
                    }
                }

                File.Move(tempPath, finalPath);
                _autoCompleterAll = null;
                return new DiagnosticOutput(new FileInfo(finalPath));
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to collect minidump");
                return null;
            }
            finally
            {
                File.Delete(tempPath);
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
                await DotTrace.EnsurePrerequisiteAsync(cancellationToken);
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

                File.Move(tempDir, finalDir);
                _autoCompleterAll = null;
                return new DiagnosticOutput(new FileInfo(finalDir));
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to collect profiling data");
                return null;
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        private static string CleanAndPrefix(string value) => string.IsNullOrWhiteSpace(value) ? "" : ("_" + PathUtils.CleanFileName(value));

        // https://docs.microsoft.com/en-us/archive/blogs/calvin_hsia/create-your-own-crash-dumps
        [DllImport("Dbghelp.dll")]
        public static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            int processId,
            SafeFileHandle hFile,
            MinidumpType dumpType,
            IntPtr exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam
        );

        //https://msdn.microsoft.com/en-us/library/windows/desktop/ms680519%28v=vs.85%29.aspx?f=255&MSPPError=-2147217396
        [Flags]
        public enum MinidumpType
        {
            Normal = 0x00000000,
            WithDataSegments = 0x00000001,
            WithFullMemory = 0x00000002,
            WithHandleData = 0x00000004,
            FilterMemory = 0x00000008,
            ScanMemory = 0x00000010,
            WithUnloadedModules = 0x00000020,
            WithIndirectlyReferencedMemory = 0x00000040,
            FilterModulePaths = 0x00000080,
            WithProcessThreadData = 0x00000100,
            WithPrivateReadWriteMemory = 0x00000200,
            WithoutOptionalData = 0x00000400,
            WithFullMemoryInfo = 0x00000800,
            WithThreadInfo = 0x00001000,
            WithCodeSegments = 0x00002000,
            WithoutAuxiliaryState = 0x00004000,
            WithFullAuxiliaryState = 0x00008000,
            WithPrivateWriteCopyMemory = 0x00010000,
            IgnoreInaccessibleMemory = 0x00020000,
            WithTokenInformation = 0x00040000,
            WithModuleHeaders = 0x00080000,
            FilterTriage = 0x00100000,
            ValidTypeFlags = 0x001fffff,
        }

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

        private readonly object _autoCompleteLock = new object();
        private long _autoCompleterExpires;
        private volatile AutoCompleteTree<DiagnosticOutput> _autoCompleterAll;

        private bool AutoCompleteExpired => _autoCompleterAll == null || Volatile.Read(ref _autoCompleterExpires) <= Stopwatch.GetTimestamp();

        private void EnsureAutoCompleter()
        {
            if (!AutoCompleteExpired)
                return;
            lock (_autoCompleteLock)
            {
                if (!AutoCompleteExpired) return;
                _autoCompleterAll = new AutoCompleteTree<DiagnosticOutput>(AllDiagnostics.Select(x => (x.Info.Name, x)));
                // 1 minute cache
                _autoCompleterExpires = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
            }
        }

        public IEnumerable<AutoCompleteTree<DiagnosticOutput>.Result> AutoCompleteDiagnostic(string prompt, int? limit = null)
        {
            EnsureAutoCompleter();
            return _autoCompleterAll.Apply(prompt, limit);
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
                    _autoCompleterAll = null;
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

    public sealed class DiagnosticFilesAutoCompleter : DiscordAutoCompleter<DiagnosticOutput>
    {
        protected override IEnumerable<AutoCompleteTree<DiagnosticOutput>.Result> Provide(AutocompleteContext ctx, string prefix)
        {
            return ctx.Services.GetRequiredService<DiagnosticController>().AutoCompleteDiagnostic(prefix);
        }

        protected override string FormatData(string key, DiagnosticOutput data) => key;

        protected override string FormatArgument(DiagnosticOutput data) => data.Info.Name;
    }
}