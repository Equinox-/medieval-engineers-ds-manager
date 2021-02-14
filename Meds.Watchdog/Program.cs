using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Meds.Shared;
using Meds.Watchdog.Database;
using Meds.Watchdog.Tasks;
using Meds.Watchdog.Utils;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Meds.Watchdog
{
    public sealed class Program : IDisposable
    {
        private readonly Logger _log = LogManager.GetCurrentClassLogger();

        public Configuration Configuration { get; }
        public PacketDistributor Distributor { get; } = new PacketDistributor();
        public PipeServer Channel { get; }

        public Influx Influx { get; }
        public HealthTracker HealthTracker { get; }
        public string InstallDirectory => Path.Combine(Configuration.Directory, "install");
        public string RuntimeDirectory => Path.Combine(Configuration.Directory, "runtime");

        private readonly FullStartTask _fullStart;

        public Program(string configFile)
        {
            Configuration = Configuration.Read(configFile);
            if (Configuration.ChannelName == null)
                Configuration.ChannelName = "meds-" + BitConverter
                    .ToString(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Configuration.Directory))))
                    .Replace("-", "");

            Influx = new Influx(Configuration.Influx);
            LogSink.Register(this);
            MetricSink.Register(this);
            HealthTracker = new HealthTracker(this);
            Channel = new PipeServer(new ChannelDesc(Configuration.ChannelName), Distributor);

            _fullStart = new FullStartTask(this);
        }

        public async Task DoWork()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            while (true)
            {
                await _fullStart.Execute();
                var startedAt = DateTime.UtcNow;
                while (true)
                {
                    var uptime = DateTime.UtcNow - startedAt;
                    if (!HealthTracker.IsRunning)
                    {
                        _log.Error($"Server has been up for {uptime} and the process disappeared.  Restarting");
                        break;
                    }

                    if (uptime.TotalSeconds > Configuration.FailureTimeout)
                    {
                        if (!HealthTracker.Liveness.State && HealthTracker.Liveness.TimeInState.TotalSeconds > Configuration.FailureTimeout)
                        {
                            _log.Error($"Server has been up for {uptime} and has not been live for {HealthTracker.Liveness.TimeInState}.  Restarting");
                            break;
                        }

                        if (!HealthTracker.Readiness.State && HealthTracker.Readiness.TimeInState.TotalSeconds > Configuration.FailureTimeout)
                        {
                            _log.Error($"Server has been up for {uptime} and has not been ready for {HealthTracker.Readiness.TimeInState}.  Restarting");
                            break;
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }

        public void Dispose()
        {
            Channel?.Dispose();
            Influx?.Dispose();
        }

        public static async Task Main(string[] args)
        {
            var config = new LoggingConfiguration();
            var consoleTarget = new ConsoleTarget("console")
            {
                Layout = "${shortdate} ${level} ${message}  ${exception} ${event-properties}"
            };
            config.AddRuleForAllLevels(consoleTarget);
            LogManager.Configuration = config;

            if (args.Length == 2 && args[0] == "restore-game-binaries")
            {
                await CiUtils.RestoreGameBinaries(args[1]);
                return;
            }

            if (args.Length < 1)
            {
                await Console.Error.WriteLineAsync("Usage: Meds.Watchdog.exe config.xml");
                return;
            }

            using (var pgm = new Program(args[0]))
            {
                await pgm.DoWork();
            }
        }
    }
}