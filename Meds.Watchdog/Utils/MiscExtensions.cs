using System.Collections.Generic;

namespace Meds.Watchdog.Utils
{
    public static class MiscExtensions
    {
        public static TV GetOrAdd<TK, TV>(this Dictionary<TK, TV> dict, TK key) where TV : new()
        {
            if (!dict.TryGetValue(key, out var result))
                // ReSharper disable once HeapView.ObjectAllocation.Possible
                dict.Add(key, result = new TV());
            return result;
        }
    }
}