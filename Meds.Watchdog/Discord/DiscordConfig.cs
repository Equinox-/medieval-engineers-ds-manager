using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using static Meds.Watchdog.Discord.DiscordPermission;

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

        // Simple policies
        Read,
        Write,
        Admin,

        // Server status
        Status,
        StatusGeneral,
        StatusPlayers,

        // Lifecycle policies
        ServerLifecycle,
        ServerLifecycleRestart,
        ServerLifecycleStop,
        ServerLifecycleStart,

        // Save file manipulation
        Saves,
        SavesCreate,

        // Watchdog
        Watchdog,
        WatchdogRestart,

        // Diagnostics
        Diagnostics,
        DiagnosticsProfile,
        DiagnosticsCoreDump,
    }

    public static class DiscordPermissionExpansion
    {
        // from permissions to suppliers
        private static readonly Dictionary<DiscordPermission, HashSet<DiscordPermission>> InverseExpansion =
            new Dictionary<DiscordPermission, HashSet<DiscordPermission>>();

        public static bool Satisfies(DiscordPermission grant, DiscordPermission request) => InverseExpansion[request].Contains(grant);

        static DiscordPermissionExpansion()
        {
            // from supplier to permissions
            var expansions = new Dictionary<DiscordPermission, DiscordPermission[]>
            {
                [Status] = new[] { StatusGeneral, StatusPlayers },
                [ServerLifecycle] = new[] { ServerLifecycleRestart, ServerLifecycleStop, ServerLifecycleStart },
                [Saves] = new[] { SavesCreate },
                [DiscordPermission.Watchdog] = new[] { WatchdogRestart },
                [Diagnostics] = new[] { DiagnosticsProfile, DiagnosticsCoreDump },

                [Read] = new[] { Status },
                [Write] = new[] { Read, Saves, Diagnostics },
                [Admin] = new[] { Write, ServerLifecycle, DiscordPermission.Watchdog }
            };
            foreach (var perm in typeof(DiscordPermission).GetEnumValues().OfType<DiscordPermission>())
                InverseExpansion.Add(perm, new HashSet<DiscordPermission> { perm });

            var explore = new Queue<DiscordPermission>();
            var visited = new HashSet<DiscordPermission>();
            foreach (var root in typeof(DiscordPermission).GetEnumValues().OfType<DiscordPermission>())
            {
                visited.Clear();
                explore.Enqueue(root);
                while (explore.Count > 0)
                {
                    var perm = explore.Dequeue();
                    if (!visited.Add(perm))
                        continue;
                    InverseExpansion[perm].Add(root);
                    if (!expansions.TryGetValue(perm, out var children))
                        continue;
                    foreach (var child in children)
                        explore.Enqueue(child);
                }
            }
        }
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
        public ulong DmGuild;

        [XmlAttribute]
        public ulong DmUser;

        [XmlAttribute]
        public ulong MentionRole;

        [XmlAttribute]
        public ulong MentionUser;
    }
}