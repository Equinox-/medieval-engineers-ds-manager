using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Profiler.SelfApi;
using Meds.Shared;
using Meds.Shared.Data;
using Meds.Watchdog.Discord;
using Meds.Watchdog.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class DiagnosticController
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        private readonly IPublisher<ChatMessage> _chatPublisher;
        private readonly Configuration _config;
        private readonly HealthTracker _healthTracker;
        private readonly ILogger<DiagnosticController> _log;

        public DiagnosticController(IPublisher<ChatMessage> chatPublisher, Configuration config, HealthTracker healthTracker,  ILogger<DiagnosticController> log)
        {
            _chatPublisher = chatPublisher;
            _config = config;
            _healthTracker = healthTracker;
            _log = log;
        }

        public struct MinidumpFile
        {
            public string Path;
            public long Size;
        }

        public async Task<MinidumpFile?> CaptureCoreDump(DateTime atUtc, string reason = null, CancellationToken cancellationToken = default)
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
                        _config.StatusChangeChannel,
                        "Core dump canceled");
                    return null;
                }

                if (Countdown.TryGetLastMessageForRemainingTime(remaining, out var newMessage) && newMessage != prevMessage)
                {
                    _chatPublisher.SendGenericMessage(
                        _config.StatusChangeChannel,
                        $"Capturing core dump in {newMessage}");
                    prevMessage = newMessage;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            try
            {
                var name = $"core_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}{CleanAndPrefix(reason)}.dmp";
                var dir = _config.DiagnosticsDirectory;
                Directory.CreateDirectory(dir);
                MinidumpFile info = default;
                info.Path = Path.Combine(dir, name);
                using (var stream = new FileStream(info.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Write))
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

                info.Size = new System.IO.FileInfo(info.Path).Length;
                return info;
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to collect minidump");
                return null;
            }
        }

        public struct ProfilingFile
        {
            public string Path;
            public long Size;
        }

        public enum ProfilingMode
        {
            Sampling,
            Timeline,
        }

        public async Task<ProfilingFile?> CaptureProfiling(
            TimeSpan duration,
            ProfilingMode mode,
            string reason = null,
            CancellationToken cancellationToken = default,
            Func<Task> starting = null)
        {
            var process = _healthTracker.ActiveProcess;
            if (process == null || process.HasExited)
                return null;
            try
            {
                var name = $"prof_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_fff}{CleanAndPrefix(reason)}";
                var dir = Path.Combine(_config.DiagnosticsDirectory, name);
                Directory.CreateDirectory(dir);
                var rootProfilePath = Path.Combine(dir, "prof.dtp");

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
                        Arguments = $"attach {process.Id} {modeArgs} --collect-data-from-start=On \"--save-to={rootProfilePath}\" --timeout={duration.TotalSeconds}s",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    }
                };
                if (!profiler.Start())
                    return null;

                _chatPublisher.SendGenericMessage(_config.StatusChangeChannel, $"Profiling for {duration.FormatHumanDuration()}");
                await (starting?.Invoke() ?? Task.CompletedTask);
                while (!profiler.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;
                    await Task.Delay(PollInterval, cancellationToken);
                }
                _chatPublisher.SendGenericMessage(_config.StatusChangeChannel, "Done profiling");

                if (!File.Exists(rootProfilePath))
                {
                    _log.ZLogWarning("Profiler output file missing");
                    Directory.Delete(dir, true);
                    return null;
                }

                ProfilingFile info = default;
                info.Path = dir;
                info.Size = 0;
                foreach (var file in Directory.GetFiles(info.Path))
                    info.Size += new System.IO.FileInfo(file).Length;
                return info;
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to collect profiling data");
                return null;
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
    }
}