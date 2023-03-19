using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Meds.Dist;

namespace Meds.Bootstrap
{
    internal class Program
    {
        // [configFile] [waitForPidToExit=none] [startWatchdog=true]
        public static async Task Main(string[] args)
        {
            var configFile = args.Length > 0 ? args[0] : DistUtils.DiscoverConfigFile();

            if (configFile == null)
            {
                await Console.Error.WriteLineAsync(
                    "Either provide a configuration file as an argument, or place one in the same folder or parent folder of Meds.Bootstrap.exe");
                return;
            }


            using var reader = File.OpenText(configFile);
            var config = (Configuration)Configuration.Serializer.Deserialize(reader);
            config.OnLoaded(configFile);
            using var log = new BootstrapLogger(config.BootstrapLog);
            try
            {
                await UpdateAndStart(config, log, args);
            }
            catch (Exception err)
            {
                log.Error($"Failed to run:\n{err}");
            }
        }

        private static async Task UpdateAndStart(Configuration config, BootstrapLogger log, string[] args)
        {
            if (config.WatchdogLayers.Count == 0)
            {
                log.Error("Most provide at least one watchdog layer");
                return;
            }

            if (args.Length >= 2)
            {
                if (!int.TryParse(args[1], out var waitForPid))
                {
                    log.Error($"Invalid wait for PID: {args[1]}");
                    return;
                }

                if (waitForPid > 0)
                {
                    log.Info($"Waiting for {waitForPid} to exit...");
                    var start = DateTime.UtcNow;
                    while (true)
                    {
                        try
                        {
                            var process = Process.GetProcessById(waitForPid);
                            if (process.HasExited)
                                break;
                        }
                        catch (ArgumentException)
                        {
                            // was not running
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }

                    log.Info($"Process {waitForPid} has exited after {DateTime.UtcNow - start}");
                }
            }

            var watchdogDir = config.WatchdogDirectory;
            Directory.CreateDirectory(watchdogDir);

            var overlays = await Task.WhenAll(config.WatchdogLayers.Select(async spec =>
            {
                var data = new OverlayData(log, watchdogDir, spec);
                await data.Load();
                return data;
            }).ToArray());

            // Clean deleted overlay files
            await Task.WhenAll(overlays.Select(overlay => Task.Run(overlay.CleanDeleted)));

            // Apply overlays
            foreach (var overlay in overlays)
                await overlay.ApplyOverlay();

            if (args.Length < 3 || !args[2].Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                var watchdogPath = Path.Combine(watchdogDir, config.WatchdogEntryPoint);
                var psi = new ProcessStartInfo
                {
                    FileName = watchdogPath,
                    WorkingDirectory = watchdogDir,
                    Arguments = $"\"{config.ConfigFile}\""
                };
                log.Info($"Starting watchdog: {psi.FileName} {psi.Arguments}");
                Process.Start(psi);
            }
        }

        private sealed class BootstrapLogger : IOverlayLogger, IDisposable
        {
            private readonly StreamWriter _writer;

            public BootstrapLogger(string path)
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                _writer = File.AppendText(path);
            }

            private void Log(string level, string msg)
            {
                Console.WriteLine(msg);
                _writer.WriteLine($"[{level}] {DateTime.Now}: {msg}");
                _writer.Flush();
            }

            public void Error(string msg) => Log("ERR", msg);

            public void Debug(string msg) => Log("DBG", msg);

            public void Info(string msg) => Log("INFO", msg);

            public void Dispose() => _writer?.Dispose();
        }
    }

    [XmlRoot]
    public class Configuration : BootstrapConfiguration
    {
        [XmlIgnore]
        public string BootstrapLog => Path.Combine(Directory, "logs/bootstrap.log");

        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Configuration));
    }
}