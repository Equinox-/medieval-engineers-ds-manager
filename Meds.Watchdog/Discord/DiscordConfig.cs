using System.Collections.Generic;
using System.Xml.Serialization;
using Equ;

namespace Meds.Watchdog.Discord
{
    public class DiscordConfig : MemberwiseEquatable<DiscordConfig>
    {
        [XmlElement]
        public string Token;

        [XmlElement]
        public string SaveSharingPassword;

        [XmlElement("ChannelSync")]
        public List<DiscordChannelSync> ChannelSyncs;

        [XmlElement("RequireGuild")]
        public List<ulong> RequireGuild;

        [XmlElement("RequireChannel")]
        public List<ulong> RequireChannel;
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

        [XmlAttribute]
        public bool DisableReuse;
    }
}