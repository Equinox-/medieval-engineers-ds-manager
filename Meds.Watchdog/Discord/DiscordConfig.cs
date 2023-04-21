using System.Collections.Generic;
using System.Xml.Serialization;
using Equ;

namespace Meds.Watchdog.Discord
{
    public class DiscordConfig : MemberwiseEquatable<DiscordConfig>
    {
        public string Token;

        [XmlElement("ChannelSync")]
        public List<DiscordChannelSync> ChannelSyncs;
    }

    public class DiscordChannelSync : MemberwiseEquatable<DiscordChannelSync>
    {
        [XmlAttribute]
        public string EventChannel;

        [XmlAttribute]
        public ulong DiscordChannel;

        [XmlAttribute]
        public ulong DmGuild;

        [XmlAttribute]
        public ulong DmUser;

        [XmlAttribute]
        public ulong MentionRole;

        [XmlAttribute]
        public ulong MentionUser;
    }
}