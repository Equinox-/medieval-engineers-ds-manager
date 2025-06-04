using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Meds.Shared
{
    public static class MinidumpUtils
    {
        // https://docs.microsoft.com/en-us/archive/blogs/calvin_hsia/create-your-own-crash-dumps
        [DllImport("Dbghelp.dll")]
        public static extern bool MiniDumpWriteDump(
            IntPtr hProcess,
            int processId,
            SafeFileHandle hFile,
            MinidumpType dumpType,
            IntPtr exceptionParam,
            IntPtr userStreamParam,
            IntPtr callbackParam
        );

        //https://msdn.microsoft.com/en-us/library/windows/desktop/ms680519%28v=vs.85%29.aspx?f=255&MSPPError=-2147217396
        [Flags]
        public enum MinidumpType
        {
            Normal = 0x00000000,
            WithDataSegments = 0x00000001,
            WithFullMemory = 0x00000002,
            WithHandleData = 0x00000004,
            FilterMemory = 0x00000008,
            ScanMemory = 0x00000010,
            WithUnloadedModules = 0x00000020,
            WithIndirectlyReferencedMemory = 0x00000040,
            FilterModulePaths = 0x00000080,
            WithProcessThreadData = 0x00000100,
            WithPrivateReadWriteMemory = 0x00000200,
            WithoutOptionalData = 0x00000400,
            WithFullMemoryInfo = 0x00000800,
            WithThreadInfo = 0x00001000,
            WithCodeSegments = 0x00002000,
            WithoutAuxiliaryState = 0x00004000,
            WithFullAuxiliaryState = 0x00008000,
            WithPrivateWriteCopyMemory = 0x00010000,
            IgnoreInaccessibleMemory = 0x00020000,
            WithTokenInformation = 0x00040000,
            WithModuleHeaders = 0x00080000,
            FilterTriage = 0x00100000,
            ValidTypeFlags = 0x001fffff,
        }

        public static bool Capture(Process process, string path, MinidumpType type = DefaultMinidumpType)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Write);
            return MiniDumpWriteDump(process.Handle, process.Id, stream.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        public static string CaptureAtomic(Process process, string dir, string name, MinidumpType type = DefaultMinidumpType)
        {
            Directory.CreateDirectory(dir);
            var finalPath = Path.Combine(dir, name);
            var tempPath = Path.Combine(dir, name + ".tmp");
            try
            {
                if (!Capture(process, tempPath))
                    return null;
                File.Move(tempPath, finalPath);
                return finalPath;
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        public const string CoreDumpExt = ".dmp";

        public const MinidumpType DefaultMinidumpType = MinidumpType.WithFullMemory | MinidumpType.WithProcessThreadData | MinidumpType.WithThreadInfo |
                                                        MinidumpType.WithUnloadedModules;
    }
}