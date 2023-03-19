using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using DSharpPlus.Entities;
using Meds.Dist;
using Meds.Shared;
using Meds.Watchdog.Discord;

namespace Meds.Watchdog
{
    [XmlRoot]
    public class Configuration : BootstrapConfiguration
    {
        public static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Configuration));
        
        [XmlElement("Wrapper")]
        public List<OverlaySpec> WrapperLayers = new List<OverlaySpec>();

        [XmlElement("WrapperEntryPoint")]
        public string WrapperEntryPoint = "DedicatedServer64\\Meds.Wrapper.exe";

        [XmlIgnore]
        public string BootstrapEntryPoint => Path.Combine(Directory, "Meds.Bootstrap.exe");

        [XmlIgnore]
        public string InstallDirectory => Path.Combine(Directory, "install");

        [XmlIgnore]
        public string RuntimeDirectory => Path.Combine(Directory, "runtime");

        [XmlIgnore]
        public string WatchdogLogs => Path.Combine(Directory, "logs/watchdog");

        [XmlIgnore]
        public string WrapperLogs => Path.Combine(Directory, "logs/wrapper");

        [XmlIgnore]
        public string DiagnosticsDirectory => Path.Combine(RuntimeDirectory, "diagnostics");

        [XmlIgnore]
        public string NamedBackupsDirectory => Path.Combine(RuntimeDirectory, "world-backups");

        /// <summary>
        /// Timeout for server shutdown, in seconds.
        /// </summary>
        [XmlElement]
        public double ShutdownTimeout = 60 * 10;

        /// <summary>
        /// Timeout for server liveness after startup, in seconds.
        /// </summary>
        [XmlElement]
        public double LivenessTimeout = 60;

        /// <summary>
        /// Timeout for server readiness after startup, in seconds.
        /// </summary>
        [XmlElement]
        public double ReadinessTimeout = 60 * 5;

        /// <summary>
        /// In-game channel to send status change messages to. 
        /// </summary>
        [XmlElement]
        public string StatusChangeChannel = "System";

        [XmlElement]
        public MessagePipe Messaging = new MessagePipe();

        [XmlElement]
        public SteamConfig Steam = new SteamConfig();

        [XmlElement]
        public MetricConfig Metrics = new MetricConfig();

        [XmlElement]
        public AdjustmentsConfig Adjustments = new AdjustmentsConfig();

        [XmlElement]
        public AuditConfig Audit = new AuditConfig();

        [XmlElement]
        public DiscordConfig Discord = new DiscordConfig();


        [XmlElement("ScheduledTask")]
        public List<ScheduledTaskConfig> ScheduledTasks;

        public class ScheduledTaskConfig
        {
            [XmlAttribute("Target")]
            public LifetimeStateCase Target = LifetimeStateCase.Faulted;

            private void MaybeSetState(bool arg, LifetimeStateCase val)
            {
                if (arg)
                    Target = val;
                else if (Target == val)
                    Target = LifetimeStateCase.Faulted;
            }

            [XmlAttribute("Shutdown")]
            public bool Shutdown
            {
                get => Target == LifetimeStateCase.Shutdown;
                set => MaybeSetState(value, LifetimeStateCase.Shutdown);
            }

            [XmlAttribute("Start")]
            public bool Start
            {
                get => Target == LifetimeStateCase.Running;
                set => MaybeSetState(value, LifetimeStateCase.Running);
            }

            [XmlAttribute("Restart")]
            public bool Restart
            {
                get => Target == LifetimeStateCase.Restarting;
                set => MaybeSetState(value, LifetimeStateCase.Restarting);
            }

            [XmlAttribute]
            public bool Utc;

            [XmlAttribute("Cron")]
            public string Cron;

            [XmlAttribute("Reason")]
            public string Reason;
        }

        public class SteamConfig
        {
            [XmlAttribute]
            public string Branch = "communityedition";
        }

        public static Configuration Read(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                var cfg = (Configuration)Serializer.Deserialize(stream);
                cfg.OnLoaded(path);

                // Assign ephemeral ports if needed.
                const int ephemeralStart = 49152;
                const int ephemeralEnd = 65530;
                var ephemeralPort = (ushort)(ephemeralStart + (path.GetHashCode() * 2503) % (ephemeralEnd - ephemeralStart));

                if (cfg.Messaging.ServerToWatchdog == 0)
                    cfg.Messaging.ServerToWatchdog = ephemeralPort;
                if (cfg.Messaging.WatchdogToServer == 0)
                    cfg.Messaging.WatchdogToServer = (ushort)(ephemeralPort + 1);

                return cfg;
            }
        }
    }
}