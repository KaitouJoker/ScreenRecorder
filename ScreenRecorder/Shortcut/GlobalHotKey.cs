using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Input;
using ScreenRecorder.Command;

namespace ScreenRecorder.Shortcut
{
    public sealed class GlobalHotKey : IDisposable
    {
        public static bool PassthroughGlobalHotKey { get; set; } = false;

        internal class HotKeyControl : System.Windows.Forms.Control
        {
            private readonly DelegateCommand _delegateCommand;
            private readonly int _id;
            private IntPtr _hWnd;
            private bool _isFallback;

            public HotKeyControl(DelegateCommand delegateCommand, int id)
            {
                _delegateCommand = delegateCommand;
                _id = id;
                RegisterHotKey();
            }

            public bool Matches(int vkCode, ModifierKeys modifiers)
            {
                if (_delegateCommand?.KeyGesture == null) return false;
                var targetVk = (int)KeyInterop.VirtualKeyFromKey(_delegateCommand.KeyGesture.Key);
                return vkCode == targetVk && _delegateCommand.KeyGesture.Modifiers == modifiers;
            }

            public void Execute()
            {
                try
                {
                    _delegateCommand?.Execute();
                }
                catch { }
            }

            protected override void WndProc(ref Message m)
            {
                try
                {
                    if (!PassthroughGlobalHotKey)
                    {
                        switch (m.Msg)
                        {
                            case WinApi.WmHotKey:
                                if (m.WParam.ToInt32() == _id)
                                {
                                    Execute();
                                }
                                m.Result = new IntPtr(1);
                                return;
                        }
                    }

                    base.WndProc(ref m);
                }
                catch { }
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                UnregisterHotKey();
            }

            private void RegisterHotKey()
            {
                try
                {
                    if (_delegateCommand?.KeyGesture == null || _delegateCommand.KeyGesture.Key == Key.None)
                        return;

                    var modifiers = _delegateCommand.KeyGesture.Modifiers;
                    var vk = (System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(_delegateCommand.KeyGesture.Key);

                    // Win32 RegisterHotKey 시도 (Windows OS에서 F12 등 시스템 예약 키나 충돌 키의 경우 false 반환)
                    if (WinApi.RegisterHotKey(Handle, _id, modifiers, vk))
                    {
                        _hWnd = Handle;
                        _isFallback = false;
                    }
                    else
                    {
                        // RegisterHotKey 실패 시 저수준 키보드 훅(WH_KEYBOARD_LL) 폴백으로 등록
                        _isFallback = true;
                        FallbackKeyboardHook.Register(this);
                    }
                }
                catch
                {
                    _isFallback = true;
                    FallbackKeyboardHook.Register(this);
                }
            }

            private void UnregisterHotKey()
            {
                try
                {
                    if (_isFallback)
                    {
                        FallbackKeyboardHook.Unregister(this);
                        _isFallback = false;
                    }
                    else if (_hWnd != IntPtr.Zero)
                    {
                        WinApi.UnregisterHotKey(_hWnd, _id);
                        _hWnd = IntPtr.Zero;
                    }
                }
                catch { }
            }
        }

        internal static class FallbackKeyboardHook
        {
            private static readonly object _lock = new object();
            private static readonly List<HotKeyControl> _fallbackControls = new List<HotKeyControl>();
            private static IntPtr _hookId = IntPtr.Zero;
            private static readonly WinApi.LowLevelKeyboardProc _proc = HookCallback; // GC 수집 방지 정적 참조

            public static void Register(HotKeyControl control)
            {
                lock (_lock)
                {
                    if (!_fallbackControls.Contains(control))
                    {
                        _fallbackControls.Add(control);
                    }
                    if (_hookId == IntPtr.Zero && _fallbackControls.Count > 0)
                    {
                        _hookId = SetHook(_proc);
                    }
                }
            }

            public static void Unregister(HotKeyControl control)
            {
                lock (_lock)
                {
                    _fallbackControls.Remove(control);
                    if (_fallbackControls.Count == 0 && _hookId != IntPtr.Zero)
                    {
                        WinApi.UnhookWindowsHookEx(_hookId);
                        _hookId = IntPtr.Zero;
                    }
                }
            }

            private static IntPtr SetHook(WinApi.LowLevelKeyboardProc proc)
            {
                try
                {
                    using (var curProcess = Process.GetCurrentProcess())
                    using (var curModule = curProcess.MainModule)
                    {
                        IntPtr hMod = WinApi.GetModuleHandle(curModule.ModuleName);
                        return WinApi.SetWindowsHookEx(WinApi.WhKeyboardLl, proc, hMod, 0);
                    }
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }

            private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0 && !PassthroughGlobalHotKey)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WinApi.WmKeyDown || msg == WinApi.WmSysKeyDown)
                    {
                        var hookStruct = Marshal.PtrToStructure<WinApi.KBDLLHOOKSTRUCT>(lParam);
                        var vk = (int)hookStruct.vkCode;

                        // Shift, Ctrl, Alt, Win 단독 키 입력은 스킵
                        if (!IsModifierKey(vk))
                        {
                            var currentModifiers = GetCurrentModifiers();

                            List<HotKeyControl> matches = null;
                            lock (_lock)
                            {
                                foreach (var ctrl in _fallbackControls)
                                {
                                    if (ctrl.Matches(vk, currentModifiers))
                                    {
                                        matches ??= new List<HotKeyControl>();
                                        matches.Add(ctrl);
                                    }
                                }
                            }

                            if (matches != null && matches.Count > 0)
                            {
                                foreach (var match in matches)
                                {
                                    match.Execute();
                                }
                                return new IntPtr(1); // 키 이벤트 소비 (단축키 처리 완료)
                            }
                        }
                    }
                }

                return WinApi.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            private static bool IsModifierKey(int vk)
            {
                return vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
                    or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
            }

            private static ModifierKeys GetCurrentModifiers()
            {
                ModifierKeys modifiers = ModifierKeys.None;
                if (IsKeyDown(0x10 /* VK_SHIFT */))
                    modifiers |= ModifierKeys.Shift;
                if (IsKeyDown(0x11 /* VK_CONTROL */))
                    modifiers |= ModifierKeys.Control;
                if (IsKeyDown(0x12 /* VK_MENU */))
                    modifiers |= ModifierKeys.Alt;
                if (IsKeyDown(0x5B /* VK_LWIN */) || IsKeyDown(0x5C /* VK_RWIN */))
                    modifiers |= ModifierKeys.Windows;

                return modifiers;
            }

            private static bool IsKeyDown(int vKey)
            {
                return (WinApi.GetAsyncKeyState(vKey) & 0x8000) != 0;
            }
        }

        internal static class WinApi
        {
            public const int WmHotKey = 0x0312;
            public const int WhKeyboardLl = 13;
            public const int WmKeyDown = 0x0100;
            public const int WmSysKeyDown = 0x0104;

            public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            public struct KBDLLHOOKSTRUCT
            {
                public uint vkCode;
                public uint scanCode;
                public uint flags;
                public uint time;
                public UIntPtr dwExtraInfo;
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool RegisterHotKey(IntPtr hWnd, int id, ModifierKeys fsModifiers, Keys vk);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll")]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr GetModuleHandle(string lpModuleName);

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int vKey);
        }

        private HotKeyControl _hotKeyControl;
        public GlobalHotKey(DelegateCommand delegateCommand)
        {
            _hotKeyControl = new HotKeyControl(delegateCommand, GetHashCode());
        }

        public void Dispose()
        {
            _hotKeyControl?.Dispose();
            _hotKeyControl = null;
        }
    }
}
