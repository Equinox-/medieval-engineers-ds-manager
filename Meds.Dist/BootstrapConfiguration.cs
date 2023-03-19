using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Meds.Dist
{
    public abstract class BootstrapConfiguration
    {
        [XmlElement("Watchdog")]
        public List<OverlaySpec> WatchdogLayers = new List<OverlaySpec>();

        [XmlElement("WatchdogEntryPoint")]
        public string WatchdogEntryPoint = "Meds.Watchdog.exe";

        [XmlIgnore]
        public string ConfigFile;

        [XmlElement]
        public string Directory;

        [XmlIgnore]
        public string BootstrapDirectory => Path.Combine(Directory, "bootstrap");

        [XmlIgnore]
        public string WatchdogDirectory => Path.Combine(Directory, "watchdog");

        public void OnLoaded(string path)
        {
            ConfigFile = path;
            Directory ??= Path.GetDirectoryName(path);
        }
    }
}