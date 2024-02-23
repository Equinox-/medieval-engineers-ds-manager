using System.Threading.Tasks;
using DSharpPlus.SlashCommands;

namespace Meds.Watchdog.Discord
{
    public abstract class DiscordCmdBase : ApplicationCommandModule
    {
        private readonly DiscordService _discord;

        protected DiscordCmdBase(DiscordService discord)
        {
            _discord = discord;
        }

        public override Task<bool> BeforeSlashExecutionAsync(InteractionContext ctx) => _discord.VerifyRequirements(ctx);

        public override Task<bool> BeforeContextMenuExecutionAsync(ContextMenuContext ctx) => _discord.VerifyRequirements(ctx);
    }
}