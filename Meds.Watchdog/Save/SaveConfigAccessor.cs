using System.Xml;

namespace Meds.Watchdog.Save
{
    public sealed class SaveConfigAccessor
    {
        public const string DefaultVersion = "0.7.3.791025";
        
        public readonly XmlElement Checkpoint;

        public string Version => Checkpoint["GameVersion"]?.InnerText ?? DefaultVersion;

        public SaveConfigAccessor(XmlElement checkpoint)
        {
            Checkpoint = checkpoint;
        }
    }
}