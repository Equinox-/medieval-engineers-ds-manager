using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace Meds.Watchdog.Utils
{
    public static class FileUtils
    {
        public static void WriteAtomic(string target, object obj, XmlSerializer serializer)
        {
            var path = Path.GetDirectoryName(target);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);
            var temp = Path.GetTempFileName();
            try
            {
                using (var stream = File.Create(temp))
                    serializer.Serialize(stream, obj);
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