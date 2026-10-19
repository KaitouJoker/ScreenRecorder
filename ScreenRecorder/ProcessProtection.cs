using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenRecorder
{
    internal static class ProcessProtection
    {
        private const int DACL_SECURITY_INFORMATION = 0x00000004;
        private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string StringSecurityDescriptor,
            uint StringSDRevision,
            out IntPtr SecurityDescriptor,
            out uint SecurityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetKernelObjectSecurity(
            IntPtr handle,
            int securityInformation,
            IntPtr pSecurityDescriptor);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Denies PROCESS_TERMINATE to prevent external anti-capture/DRM processes
        /// (e.g. StarPlayerAgent's TerminateProcess) from forcibly killing ScreenRecorder.
        /// </summary>
        public static bool ProtectFromTermination()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
                if (hProcess == IntPtr.Zero)
                    return false;

                try
                {
                    // D:(D;;0x0001;;;WD): Deny PROCESS_TERMINATE (0x0001) to Everyone (WD)
                    if (ConvertStringSecurityDescriptorToSecurityDescriptor("D:(D;;0x0001;;;WD)", 1, out IntPtr pSD, out _))
                    {
                        return SetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, pSD);
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch { }
            return false;
        }
    }
}
