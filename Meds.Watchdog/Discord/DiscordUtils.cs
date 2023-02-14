using System;
using System.Text;
using Meds.Shared.Data;

namespace Meds.Watchdog.Discord
{
    public enum DiscordTimeFormat : byte
    {
        ShortDate = (byte)'d',
        LongDate = (byte)'D',
        ShortTime = (byte)'t',
        LongTime = (byte)'T',
        ShortDateTime = (byte)'f',
        LongDateTime = (byte)'F',
        Relative = (byte)'R'
    }

    public static class DiscordUtils
    {
        public static string AsDiscordTime(this DateTime timeUtc, DiscordTimeFormat format = DiscordTimeFormat.ShortDateTime)
        {
            return $"<t:{((DateTimeOffset)timeUtc).ToUnixTimeSeconds()}:{(char)format}>";
        }

        public static string RenderPlayerForDiscord(this PlayerResponse player)
        {
            var response = new StringBuilder();
            if (player.FactionTag != null)
                response.Append("[").Append(player.FactionTag).Append("] ");
            response.Append(player.Name);
            switch (player.Promotion)
            {
                case PlayerPromotionLevel.Moderator:
                    response.Append(" (Mod)");
                    break;
                case PlayerPromotionLevel.Admin:
                    response.Append(" (Admin)");
                    break;
                case PlayerPromotionLevel.None:
                default:
                    break;
            }

            return response.ToString();
        }
    }
}