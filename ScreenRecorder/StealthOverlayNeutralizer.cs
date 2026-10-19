using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenRecorder
{
    /// <summary>
    /// Neutralizes DRM blackout overlays (e.g. StarPlayerAgent's 'OverlayClass' window
    /// created with SetWindowDisplayAffinity=WDA_MONITOR) by moving them off-screen
    /// and hiding them. This allows the underlying browser HTML5 video to be captured cleanly
    /// without triggering StarPlayer's anti-capture alarms or halting playback.
    /// </summary>
    internal static class StealthOverlayNeutralizer
    {
        private const int SW_HIDE = 0;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static Timer _timer;
        private static int _running;

        public static void Start()
        {
            if (_timer != null)
                return;

            _timer = new Timer(OnTick, null, 0, 200);
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
                EnumWindows((hwnd, lParam) =>
                {
                    try
                    {
                        var sb = new StringBuilder(64);
                        if (GetClassName(hwnd, sb, 64) > 0)
                        {
                            string cls = sb.ToString();
                            if (cls.Equals("OverlayClass", StringComparison.OrdinalIgnoreCase))
                            {
                                // StarPlayer's DRM blackout overlay detected.
                                // Move off-screen and hide so DWM does not black out the monitor capture.
                                SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 1, 1, SWP_NOZORDER | SWP_NOACTIVATE);
                                ShowWindow(hwnd, SW_HIDE);
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
    }
}
