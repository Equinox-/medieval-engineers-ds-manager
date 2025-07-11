using System.IO;
using System.Xml.Serialization;
using Meds.Dist;
using Meds.Shared;

namespace Meds.Watchdog
{
    public abstract class InstallConfiguration : BootstrapConfiguration
    {
        [XmlElement]
        public MessagePipe Messaging = new MessagePipe();

        [XmlAttribute]
        public string Instance;

        [XmlIgnore]
        public string BootstrapEntryPoint => Path.Combine(Directory, "Meds.Bootstrap.exe");

        [XmlIgnore]
        public string InstallDirectory => Path.Combine(Directory, "install");

        [XmlIgnore]
        public string RuntimeDirectory => Path.Combine(Directory, "runtime");

        [XmlIgnore]
        public string LogsDirectory => Path.Combine(Directory, "logs");

        [XmlIgnore]
        public string WatchdogLogs => Path.Combine(LogsDirectory, "watchdog");

        [XmlIgnore]
        public string WrapperLogs => Path.Combine(LogsDirectory, "wrapper");

        [XmlIgnore]
        public string DiagnosticsDirectory => Path.Combine(Directory, "diagnostics");

        [XmlIgnore]
        public string ArchivedBackupsDirectory => Path.Combine(Directory, "named-backups");

        [XmlIgnore]
        public string GrafanaAgentDirectory => Path.Combine(Directory, "grafana-agent");

        [XmlIgnore]
        public string DedicatedServerConfigFile => Path.Combine(RuntimeDirectory, "MedievalEngineersDedicated-Dedicated.cfg");

        [XmlIgnore]
        public string WorldDirectory => Path.Combine(RuntimeDirectory, "world");

        [XmlIgnore]
        public string WorldConfigFile => Path.Combine(WorldDirectory, "Sandbox.sbc");

        public override void OnLoaded(string path)
        {
            base.OnLoaded(path);

            // Assign ephemeral ports if needed.
            const int ephemeralStart = 49152;
            const int ephemeralEnd = 65530;
            var ephemeralPort = (ushort)(ephemeralStart + (path.GetHashCode() * 2503) % (ephemeralEnd - ephemeralStart));

            if (Messaging.ServerToWatchdog == 0)
                Messaging.ServerToWatchdog = ephemeralPort;
            if (Messaging.WatchdogToServer == 0)
                Messaging.WatchdogToServer = (ushort)(ephemeralPort + 1);

            Instance ??= Path.GetFileName(Path.GetDirectoryName(path))?.ToLowerInvariant();
        }
    }
}