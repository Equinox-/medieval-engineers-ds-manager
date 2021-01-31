using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Meds.Shared
{
    public static class PipeUtils
    {
        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern bool CancelSynchronousIo(IntPtr threadHandle);

        public static uint CurrentNativeThreadId => GetCurrentThreadId();

        public static bool CancelSynchronousIo(uint threadId)
        {
            if (threadId == 0)
                return false;
            try
            {
                var handle = OpenThread(0x40000000, false, threadId);
                try
                {
                    return CancelSynchronousIo(handle);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch (Exception err)
            {
                // ignore errors
                return false;
            }
        }

        public static bool IsPipeClosedError(Exception err)
        {
            return err is OperationCanceledException || err is ObjectDisposedException;
        }

        public static ThreadStart WrapPipeThread(ThreadStart action)
        {
            return () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (IsPipeClosedError(ex))
                {
                    // ignored
                }
            };
        }
    }
}