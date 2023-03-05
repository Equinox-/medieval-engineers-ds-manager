using System;
using System.IO;

namespace Meds.Watchdog.Utils
{
    public static class PathUtils
    {

        private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

        public static string CleanFileName(string message)
        {
            Span<char> clean = stackalloc char[message.Length];
            message.AsSpan().CopyTo(clean);
            var unsafeChars = InvalidFileChars.AsSpan();
            for (var i = 0; i < message.Length; i++)
            {
                var c = message[i];
                if (unsafeChars.IndexOf(c) >= 0)
                    c = '_';
                clean[i] = c;
            }

            return clean.ToString();
        }
    }
}