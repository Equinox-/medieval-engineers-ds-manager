using System;

namespace Meds.Shared
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

    public static class DiscordTimeUtils
    {
        public static string AsDiscordTime(this DateTime timeUtc, DiscordTimeFormat format = DiscordTimeFormat.ShortDateTime)
        {
            return ((DateTimeOffset)timeUtc).AsDiscordTime(format);
        }

        public static string AsDiscordTime(this DateTimeOffset timeUtc, DiscordTimeFormat format = DiscordTimeFormat.ShortDateTime)
        {
            return $"<t:{timeUtc.ToUnixTimeSeconds()}:{(char)format}>";
        }
    }
}