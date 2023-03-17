using System.Collections.Generic;
using System.Xml.Serialization;

namespace Meds.Watchdog.Discord
{
    public class DiscordConfig
    {
        public string Token;

        [XmlElement("Grant")]
        public List<DiscordPermissionGrant> Grants;

        [XmlElement("ChannelSync")]
        public List<DiscordChannelSync> ChannelSyncs;
    }

    public enum DiscordPermission
    {
        None,
        Read,
        Write,
        Admin,
    }

    public struct DiscordPermissionGrant
    {
        [XmlAttribute]
        public DiscordPermission Perm;

        [XmlAttribute]
        public ulong Role;

        [XmlAttribute]
        public ulong User;

        [XmlAttribute]
        public ulong Guild;

        [XmlAttribute]
        public ulong Channel;
    }

    public class DiscordChannelSync
    {
        [XmlAttribute]
        public string EventChannel;

        [XmlAttribute]
        public ulong DiscordChannel;

        [XmlAttribute]
        public bool ToDiscord;

        [XmlAttribute]
        public ulong MentionRole;

        [XmlAttribute]
        public ulong MentionUser;
    }
}