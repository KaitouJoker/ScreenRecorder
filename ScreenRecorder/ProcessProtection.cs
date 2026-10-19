using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenRecorder
{
    internal static class ProcessProtection
    {
        private const int DACL_SECURITY_INFORMATION = 0x00000004;
        private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetKernelObjectSecurity(
            IntPtr handle,
            int securityInformation,
            IntPtr pSecurityDescriptor,
            uint nLength,
            out uint lpnLengthNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
            IntPtr SecurityDescriptor,
            uint RequestedStringSDRevision,
            int SecurityInformation,
            out IntPtr StringSecurityDescriptor,
            out uint StringSecurityDescriptorLen);

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
        /// (e.g. StarPlayerAgent's TerminateProcess) from forcibly killing ScreenRecorder,
        /// while preserving all existing Allow ACEs so that DWM, DirectX, and Windows subsystems
        /// can continue normal process and capture operations.
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
                    string origDacl = null;
                    if (GetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, IntPtr.Zero, 0, out uint needed) || needed > 0)
                    {
                        IntPtr pSD = Marshal.AllocHGlobal((int)needed);
                        try
                        {
                            if (GetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, pSD, needed, out _))
                            {
                                if (ConvertSecurityDescriptorToStringSecurityDescriptor(pSD, 1, DACL_SECURITY_INFORMATION, out IntPtr strSD, out _))
                                {
                                    origDacl = Marshal.PtrToStringUni(strSD);
                                    Marshal.FreeHGlobal(strSD);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pSD);
                        }
                    }

                    string newSddl;
                    if (!string.IsNullOrEmpty(origDacl) && origDacl.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Prepend Deny PROCESS_TERMINATE (0x0001) to Everyone (WD) before existing Allow ACEs
                        newSddl = "D:(D;;0x0001;;;WD)" + origDacl.Substring(2);
                    }
                    else
                    {
                        // Fallback: Deny PROCESS_TERMINATE to Everyone, but explicitly Allow Owner, Interactive User, System, Administrators
                        newSddl = "D:(D;;0x0001;;;WD)(A;;GA;;;OW)(A;;GA;;;IU)(A;;GA;;;SY)(A;;GA;;;BA)";
                    }

                    if (ConvertStringSecurityDescriptorToSecurityDescriptor(newSddl, 1, out IntPtr newSD, out _))
                    {
                        return SetKernelObjectSecurity(hProcess, DACL_SECURITY_INFORMATION, newSD);
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProcessProtection Exception]: {ex.Message}");
            }
            return false;
        }
    }
}
