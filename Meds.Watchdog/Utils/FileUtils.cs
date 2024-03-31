using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace Meds.Watchdog.Utils
{
    public static class FileUtils
    {
        public static void WriteAtomic(string target, object obj, XmlSerializer serializer) => WriteAtomic(target,
            stream => serializer.Serialize(stream, obj));

        public static void WriteAtomic(string target, string content) => WriteAtomic(target,
            stream =>
            {
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            });

        public static void WriteAtomic(string target, Action<Stream> writer)
        {
            var path = Path.GetDirectoryName(target);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);
            var temp = Path.Combine(path ?? "", Path.GetFileName(target) + ".tmp");
            try
            {
                using (var stream = File.Create(temp))
                    writer(stream);
                if (!MoveFileEx(temp, target, MoveFileExFlags.ReplaceExisting | MoveFileExFlags.WriteThrough))
                    throw new Exception(
                        $"Failed to move file: {Marshal.GetLastWin32Error()}",
                        Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, MoveFileExFlags flags);

        [Flags]
        private enum MoveFileExFlags
        {
            ReplaceExisting = 1,
            CopyAllowed = 2,
            DelayUntilReboot = 4,
            WriteThrough = 8,
            CreateHardlink = 16,
            FailIfNotTrackable = 32,
        }
    }
}