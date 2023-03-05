using System;
using System.Collections.Generic;

namespace Meds.Watchdog.Utils
{
    public static class Countdown
    {
        private static readonly List<(TimeSpan, string)> MessageForDuration = new List<(TimeSpan, string)>
        {
            (TimeSpan.FromSeconds(5), "5 seconds"),
            (TimeSpan.FromSeconds(10), "10 seconds"),
            (TimeSpan.FromSeconds(15), "15 seconds"),
            (TimeSpan.FromSeconds(30), "30 seconds"),
            (TimeSpan.FromMinutes(1), "1 minute"),
            (TimeSpan.FromMinutes(2), "2 minutes"),
            (TimeSpan.FromMinutes(3), "3 minutes"),
            (TimeSpan.FromMinutes(4), "4 minutes"),
            (TimeSpan.FromMinutes(5), "5 minutes"),
            (TimeSpan.FromMinutes(10), "10 minutes"),
            (TimeSpan.FromMinutes(15), "15 minutes"),
            (TimeSpan.FromMinutes(20), "20 minutes"),
            (TimeSpan.FromMinutes(25), "25 minutes"),
            (TimeSpan.FromMinutes(30), "30 minutes")
        };

        private class MessageForDurationComparer : IComparer<(TimeSpan, string)>
        {
            public static readonly MessageForDurationComparer Instance = new MessageForDurationComparer();

            public int Compare((TimeSpan, string) x, (TimeSpan, string) y) => x.Item1.CompareTo(y.Item1);
        }

        public static bool TryGetLastMessageForRemainingTime(TimeSpan remaining, out string message)
        {
            message = default;
            var idx = MessageForDuration.BinarySearch((remaining, null), MessageForDurationComparer.Instance);
            if (idx < 0)
                idx = ~idx;
            if (idx >= MessageForDuration.Count) 
                return false;
            message = MessageForDuration[idx].Item2;
            return true;
        }
    }
}