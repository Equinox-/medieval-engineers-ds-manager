using System.Collections.Generic;
using DSharpPlus.Entities;

using static Meds.Watchdog.Discord.DiscordPermissionExpansion;

namespace Meds.Watchdog.Discord
{
    public sealed class DiscordPermissionController
    {
        private readonly DiscordPermission _globalPermission;
        private readonly Dictionary<ulong, DiscordPermission> _grantByRole = new Dictionary<ulong, DiscordPermission>();
        private readonly Dictionary<ulong, DiscordPermission> _grantByUser = new Dictionary<ulong, DiscordPermission>();
        private readonly Dictionary<ulong, DiscordPermission> _grantByChannel = new Dictionary<ulong, DiscordPermission>();

        public DiscordPermissionController(Configuration config)
        {
            var grants = config.Discord?.Grants;
            if (grants == null) return;
            _globalPermission = DiscordPermission.None;
            foreach (var grant in grants)
            {
                if (grant.User != 0)
                    _grantByUser.Add(grant.User, grant.Perm);
                if (grant.Role != 0)
                    _grantByRole.Add(grant.Role, grant.Perm);
                if (grant.Channel != 0)
                    _grantByChannel.Add(grant.Channel, grant.Perm);
                if (grant is { User: 0, Role: 0, Channel: 0 })
                    _globalPermission = grant.Perm;
            }
        }

        public bool Check(DiscordPermission request, DiscordUser user, DiscordMember member, DiscordChannel channel = null)
        {
            if (_grantByUser.TryGetValue(user.Id, out var userGrant) && Satisfies(userGrant, request))
                return true;
            if (channel != null && _grantByChannel.TryGetValue(channel.Id, out var channelGrant) && Satisfies(channelGrant, request))
                return true;
            // ReSharper disable once InvertIf
            if (member != null)
                foreach (var role in member.Roles)
                    if (_grantByRole.TryGetValue(role.Id, out var roleGrant) && Satisfies(roleGrant, request))
                        return true;
            return Satisfies(_globalPermission, request);
        }
    }
}