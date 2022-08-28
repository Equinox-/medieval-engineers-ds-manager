using System;

namespace Meds.Shared
{
    public static class MiscUtils
    {
        public static Func<T> Memoize<T>(Func<T> supplier)
        {
            var init = new bool[1];
            var tmp = new T[1];
            return () =>
            {
                if (init[0])
                    return tmp[0];
                lock (init)
                {
                    if (init[0])
                        return tmp[0];
                    tmp[0] = supplier();
                    init[0] = true;
                    return tmp[0];
                }
            };
        }
    }
}