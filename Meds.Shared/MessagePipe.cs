using System.Xml.Serialization;
using Equ;

namespace Meds.Shared
{
    public class MessagePipe : MemberwiseEquatable<MessagePipe>
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