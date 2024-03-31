using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meds.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog.GrafanaAgent
{
    public class GaController : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan PollIntervalMax = TimeSpan.FromDays(1);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan StartPollInterval = TimeSpan.FromSeconds(1);

        private readonly Refreshable<GaRenderedConfig> _cfg;
        private readonly ILogger<GaController> _log;
        private readonly InstallConfiguration _install;
        private readonly string _exeFile;
        private readonly string _exeFileVersion;
        private readonly string _exeFileTemp;
        private readonly string _cfgFile;

        public GaController(InstallConfiguration install, Refreshable<GaRenderedConfig> cfg, ILogger<GaController> log)
        {
            _cfg = cfg;
            _log = log;
            _install = install;

            _exeFile = Path.Combine(_install.GrafanaAgentDirectory, "agent.exe");
            _exeFileVersion = _exeFile + ".version";
            _exeFileTemp = _exeFile + ".temp";
            _cfgFile = Path.Combine(_install.GrafanaAgentDirectory, "agent.cfg");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var cfgUpdated = new[] { new TaskCompletionSource<byte>() };
            using var cfgToken = _cfg.Subscribe(_ => cfgUpdated[0].SetResult(0));

            var pollInterval = PollInterval;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.WhenAny(cfgUpdated[0].Task, Task.Delay(pollInterval, stoppingToken));
                    if (cfgUpdated[0].Task.IsCompleted)
                        cfgUpdated[0] = new TaskCompletionSource<byte>();

                    var okay = await Enforce(_cfg.Current, stoppingToken);
                    pollInterval = okay ? PollInterval : TimeSpan.FromTicks(Math.Max(PollIntervalMax.Ticks, pollInterval.Ticks * 2));
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task<bool> Enforce(GaRenderedConfig cfg, CancellationToken ct)
        {
            if (!cfg.Enabled)
            {
                if (ct.IsCancellationRequested) return false;
                await EnforceShutdown();
                return true;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                await EnforceVersion(cfg.BinaryUrl, ct);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to enforce agent binary version");
                return false;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                await EnforceConfig(cfg.ConfigContent, ct);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception err)
            {
                _log.ZLogWarning(err, "Failed to enforce agent config");
                return false;
            }

            return await EnforceRunning(ct);
        }

        private Task EnforceShutdown()
        {
            var process = FindActiveProcess();
            if (process == null) return Task.CompletedTask;

            _log.ZLogInformation("Shutting down grafana agent, pid={0}", process.Id);
            process.Kill();
            return Task.CompletedTask;
        }

        [DllImport("User32.dll")]
        private static extern bool SetWindowText(IntPtr windowHandle, string title);

        private async Task<bool> EnforceRunning(CancellationToken ct)
        {
            var process = FindActiveProcess();
            if (process is { HasExited: false })
                return true;

            var startInfo = new ProcessStartInfo
            {
                WorkingDirectory = _install.Directory,
                Arguments = $"-config.file \"{_cfgFile}\"",
                FileName = _exeFile,
            };
            process = Process.Start(startInfo);
            if (process == null)
            {
                _log.ZLogError("Failed to start grafana agent", startInfo.FileName, startInfo.Arguments);
                return false;
            }

            _log.ZLogInformation("Launched grafana agent, pid={0}", process.Id);
            var start = DateTime.Now;
            var hasRenamed = false;
            while (DateTime.Now < start + StartTimeout)
            {
                process = FindActiveProcess();
                if (process == null || process.HasExited)
                {
                    _log.ZLogError("Grafana agent terminated too quickly. Is the generated configuration correct?");
                    return false;
                }

                if (!hasRenamed)
                {
                    var mainWindowHandle = process.MainWindowHandle;
                    hasRenamed = mainWindowHandle != IntPtr.Zero && SetWindowText(mainWindowHandle, $"[{_install.Instance}] Grafana Agent");
                }

                await Task.Delay(StartPollInterval, ct);
            }

            return true;
        }

        private async Task EnforceConfig(string config, CancellationToken ct)
        {
            var configUtf8 = Encoding.UTF8.GetBytes(config);
            var sha256 = SHA256.Create();
            if (File.Exists(_cfgFile))
            {
                using var existing = File.Open(_cfgFile, FileMode.Open, FileAccess.Read);
                var existingHash = sha256.ComputeHash(existing);
                var desiredHash = sha256.ComputeHash(configUtf8);
                if (existingHash.AsSpan().SequenceEqual(desiredHash.AsSpan()))
                    return;
            }

            ct.ThrowIfCancellationRequested();
            await EnforceShutdown();
            using var configStream = File.Open(_cfgFile, FileMode.Create, FileAccess.Write);
            await configStream.WriteAsync(configUtf8, 0, configUtf8.Length, ct);
        }

        private async Task EnforceVersion(string binaryUrl, CancellationToken ct)
        {
            if (File.Exists(_exeFile) && IsExe(_exeFile) && File.Exists(_exeFileVersion) && File.ReadAllText(_exeFileVersion) == binaryUrl)
                return;

            try
            {
                _log.ZLogInformation("Updating grafana agent binary from {0}", binaryUrl);
                var request = WebRequest.Create(binaryUrl);
                using (ct.Register(request.Abort, false))
                using (var src = await request.GetResponseAsync())
                using (var srcStream = src.GetResponseStream())
                using (var dst = File.Open(_exeFileTemp, FileMode.Create, FileAccess.Write))
                {
                    ct.ThrowIfCancellationRequested();
                    if (srcStream == null)
                        throw new Exception($"Failed to load grafana agent binary from {binaryUrl}");
                    await srcStream.CopyToAsync(dst, 81920, ct);
                }

                ct.ThrowIfCancellationRequested();
                await EnforceShutdown();

                if (IsZip(_exeFileTemp))
                    await UnzipSingleExe(_exeFileTemp, _exeFile, ct);
                else if (IsExe(_exeFileTemp))
                    File.Move(_exeFileTemp, _exeFile);
                else
                    throw new Exception("Downloaded file is not a ZIP or EXE");

                ct.ThrowIfCancellationRequested();
                File.WriteAllText(_exeFileVersion, binaryUrl);
                _log.ZLogInformation("Updated grafana agent binary");
            }
            finally
            {
                File.Delete(_exeFileTemp);
            }
        }

        private static bool IsZip(string file)
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read);
            var buf = new byte[4];
            if (stream.Read(buf, 0, 4) != 4)
                return false;
            return BitConverter.ToUInt32(buf, 0) == 0x04034b50u;
        }

        private static bool IsExe(string file)
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read);
            return IsExe(stream);
        }

        private static bool IsExe(Stream stream)
        {
            var buf = new byte[2];
            if (stream.Read(buf, 0, 2) != 2)
                return false;
            return BitConverter.ToUInt16(buf, 0) == 0x5a4du;
        }

        private static async Task UnzipSingleExe(string zipFile, string target, CancellationToken ct)
        {
            ZipArchiveEntry singleEntry = null;
            using var stream = File.Open(zipFile, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
                if (entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (singleEntry != null)
                        throw new Exception("Zip file contains multiple EXEs");
                    singleEntry = entry;
                }

            if (singleEntry == null)
                throw new Exception("Zip file does not contain an EXE");
            using (var magicStream = singleEntry.Open())
                if (!IsExe(magicStream))
                    throw new Exception("Zip file exe does not look right");
            using var srcStream = singleEntry.Open();
            using var targetStream = File.Open(target, FileMode.Create, FileAccess.Write);
            await srcStream.CopyToAsync(targetStream, 81920, ct);
        }

        private Process FindActiveProcess()
        {
            var expectedPath = Path.GetFullPath(_exeFile);
            var processName = Path.GetFileNameWithoutExtension(expectedPath);
            var processes = Process.GetProcessesByName(processName);
            foreach (var proc in processes)
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (path != null && Path.GetFullPath(path) == expectedPath)
                        return proc;
                }
                catch
                {
                    // ignore
                }
            }

            return null;
        }
    }
}