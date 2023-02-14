using System;
using System.Threading.Tasks;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Meds.Watchdog.Discord
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RequirePermissionAttribute : CheckBaseAttribute
    {
        private readonly DiscordPermission _permission;

        public RequirePermissionAttribute(DiscordPermission perm)
        {
            _permission = perm;
        }

        public override Task<bool> ExecuteCheckAsync(CommandContext ctx, bool help)
        {
            return Task.FromResult(ctx.CommandsNext.Services.GetRequiredService<DiscordPermissionController>()
                .Check(_permission, ctx.User, ctx.Member, ctx.Message.Channel));
        }
    }
}