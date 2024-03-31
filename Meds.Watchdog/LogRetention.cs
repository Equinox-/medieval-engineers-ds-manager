using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Watchdog
{
    public sealed class LogRetention : BackgroundService
    {
        private const int KeepFiles = 64;
        private static readonly TimeSpan RetentionInterval = TimeSpan.FromDays(1);

        private readonly string _logsRoot;
        private readonly ILogger<LogRetention> _log;

        public LogRetention(InstallConfiguration install, ILogger<LogRetention> log)
        {
            _logsRoot = install.LogsDirectory;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RunRetention();
                try
                {
                    await Task.Delay(RetentionInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void RunRetention()
        {
            var logDirectories = Directory.GetDirectories(_logsRoot);
            foreach (var logDir in logDirectories)
            {
                var logDirName = Path.GetFileName(logDir);
                var logFiles = Directory.GetFiles(logDir, "*.log");
                if (logFiles.Length < KeepFiles)
                    continue;
                Array.Sort(logFiles);

                // Due to file names the log files are now ordered oldest -> newest.
                for (var i = 0; i < logFiles.Length - KeepFiles; i++)
                {
                    _log.ZLogInformation("Deleting log file {0}/{1}", logDirName, Path.GetFileName(logFiles[i]));
                    File.Delete(logFiles[i]);
                }
            }
        }
    }
}