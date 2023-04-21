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
        public string ArchivedBackupsDirectory => Path.Combine(Directory, "named-backups");

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
        }
    }
}