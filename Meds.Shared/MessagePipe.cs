using System.Xml.Serialization;

namespace Meds.Shared
{
    public class MessagePipe
    {
        [XmlAttribute]
        public ushort WatchdogToServer;

        [XmlAttribute]
        public ushort ServerToWatchdog;

        [XmlAttribute]
        public ushort Port
        {
            set
            {
                WatchdogToServer = value;
                ServerToWatchdog = (ushort)(value + 1);
            }
        }
    }
}