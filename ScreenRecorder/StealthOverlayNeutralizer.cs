using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenRecorder
{
    /// <summary>
    /// Neutralizes DRM blackout protections:
    /// 1. StarPlayerAgent's 'OverlayClass' blackout dummy overlay: moved off-screen and hidden.
    /// 2. Browser windows (Chrome, Edge, etc.) where StarPlayerAgent injected SetWindowDisplayAffinity(WDA_MONITOR):
    ///    clears the affinity back to WDA_NONE (0) via remote thread execution.
    /// </summary>
    internal static class StealthOverlayNeutralizer
    {
        private const int SW_HIDE = 0;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        private static Timer _timer;
        private static int _running;

        public static void Start()
        {
            if (_timer != null)
                return;

            _timer = new Timer(OnTick, null, 0, 300);
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        private static void OnTick(object state)
        {
            if (!AppConfig.Instance.EnableStealthMode)
                return;

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return;

            try
            {
                int currentPid = Process.GetCurrentProcess().Id;

                EnumWindows((hwnd, lParam) =>
                {
                    try
                    {
                        var sb = new StringBuilder(64);
                        if (GetClassName(hwnd, sb, 64) > 0)
                        {
                            string cls = sb.ToString();

                            // 1. StarPlayerAgent's DRM blackout dummy overlay
                            if (cls.Equals("OverlayClass", StringComparison.OrdinalIgnoreCase))
                            {
                                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1, SWP_NOZORDER | SWP_NOACTIVATE);
                                ShowWindow(hwnd, SW_HIDE);
                            }
                        }

                        // 2. Check for any window with non-zero DisplayAffinity (WDA_MONITOR)
                        if (GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity != 0)
                        {
                            GetWindowThreadProcessId(hwnd, out uint pid);
                            if (pid == (uint)currentPid)
                            {
                                // Our own window
                                SetWindowDisplayAffinity(hwnd, 0);
                            }
                            else
                            {
                                // Target browser window (e.g. Chrome, Edge) that StarPlayerAgent injected WDA_MONITOR into
                                ClearDisplayAffinityRemote(hwnd, pid);
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static bool ClearDisplayAffinityRemote(IntPtr hwnd, uint pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero)
                return false;

            try
            {
                IntPtr hUser32 = GetModuleHandle("user32.dll");
                IntPtr pSetAffinity = GetProcAddress(hUser32, "SetWindowDisplayAffinity");
                if (pSetAffinity == IntPtr.Zero)
                    return false;

                // 33-byte x64 shellcode calling SetWindowDisplayAffinity(hwnd, 0):
                // 48 B9 <hwnd:8>       mov rcx, hwnd
                // 31 D2                xor edx, edx (dwAffinity = 0)
                // 48 B8 <func:8>       mov rax, SetWindowDisplayAffinity
                // 48 83 EC 28          sub rsp, 40
                // FF D0                call rax
                // 48 83 C4 28          add rsp, 40
                // C3                   ret
                byte[] code = new byte[33]
                {
                    0x48, 0xB9, 0, 0, 0, 0, 0, 0, 0, 0,
                    0x31, 0xD2,
                    0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,
                    0x48, 0x83, 0xEC, 0x28,
                    0xFF, 0xD0,
                    0x48, 0x83, 0xC4, 0x28,
                    0xC3
                };

                long hwndVal = hwnd.ToInt64();
                long funcVal = pSetAffinity.ToInt64();
                byte[] hwndBytes = BitConverter.GetBytes(hwndVal);
                byte[] funcBytes = BitConverter.GetBytes(funcVal);
                Array.Copy(hwndBytes, 0, code, 2, 8);
                Array.Copy(funcBytes, 0, code, 14, 8);

                IntPtr remoteMem = VirtualAllocEx(hProc, IntPtr.Zero, (uint)code.Length, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (remoteMem == IntPtr.Zero)
                    return false;

                try
                {
                    if (!WriteProcessMemory(hProc, remoteMem, code, (uint)code.Length, out _))
                        return false;

                    IntPtr hThread = CreateRemoteThread(hProc, IntPtr.Zero, 0, remoteMem, IntPtr.Zero, 0, out _);
                    if (hThread == IntPtr.Zero)
                        return false;

                    WaitForSingleObject(hThread, 2000);
                    CloseHandle(hThread);
                    System.Diagnostics.Debug.WriteLine($"[StealthOverlayNeutralizer] Successfully reset DisplayAffinity on HWND 0x{hwndVal:X} (PID {pid})");
                    return true;
                }
                finally
                {
                    VirtualFreeEx(hProc, remoteMem, 0, MEM_RELEASE);
                }
            }
            finally
            {
                CloseHandle(hProc);
            }
        }
    }
}
