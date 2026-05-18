using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroKeyboard.Services
{
    /// <summary>
    /// 全局键盘/鼠标钩子管理器，使用 Win32 SetWindowsHookEx
    /// </summary>
    public class GlobalHookManager : IDisposable
    {
        #region Win32 API

        private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public int x;
            public int y;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        #endregion

        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;
        private LowLevelHookProc? _keyboardProc;
        private LowLevelHookProc? _mouseProc;
        private bool _disposed;

        // 标记：是否由 MacroPlayer 注入的事件（通过 dwExtraInfo 区分）
        public static readonly IntPtr INJECTED_FLAG = new IntPtr(0x4D41_4352); // "MACR"

        public event Action<int, bool>? KeyEvent;        // (vkCode, isDown)
        public event Action<int, int, int, bool>? MouseButtonEvent; // (x, y, button, isDown)
        public event Action<int, int>? MouseMoveEvent;   // (x, y)
        public event Action<int, int, int>? MouseWheelEvent; // (x, y, delta)

        public bool IsKeyboardHooked => _keyboardHook != IntPtr.Zero;
        public bool IsMouseHooked => _mouseHook != IntPtr.Zero;

        // 是否拦截（吞掉）回放触发键
        public Func<int, bool, bool>? ShouldSuppressKey { get; set; }
        // 是否拦截鼠标按键（用于鼠标触发键）
        public Func<int, bool, bool>? ShouldSuppressMouseButton { get; set; }

        public void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero) return;
            _keyboardProc = KeyboardHookCallback;
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(module.ModuleName), 0);
        }

        public void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _mouseProc = MouseHookCallback;
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(module.ModuleName), 0);
        }

        public void UninstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }

        public void UninstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // 忽略自己注入的事件
                if (info.dwExtraInfo == INJECTED_FLAG)
                    return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    // 先触发 KeyEvent，让主逻辑有机会处理（如停止回放）
                    KeyEvent?.Invoke((int)info.vkCode, isDown);

                    // 再检查是否需要拦截（防止事件传给其他应用）
                    if (ShouldSuppressKey?.Invoke((int)info.vkCode, isDown) == true)
                        return new IntPtr(1); // 吞掉事件
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // 忽略自己注入的事件
                if (info.dwExtraInfo == INJECTED_FLAG)
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                switch (msg)
                {
                    case WM_LBUTTONDOWN:
                        MouseButtonEvent?.Invoke(info.x, info.y, 0, true);
                        if (ShouldSuppressMouseButton?.Invoke(0, true) == true)
                            return new IntPtr(1);
                        break;
                    case WM_LBUTTONUP:
                        MouseButtonEvent?.Invoke(info.x, info.y, 0, false);
                        if (ShouldSuppressMouseButton?.Invoke(0, false) == true)
                            return new IntPtr(1);
                        break;
                    case WM_RBUTTONDOWN:
                        MouseButtonEvent?.Invoke(info.x, info.y, 1, true);
                        if (ShouldSuppressMouseButton?.Invoke(1, true) == true)
                            return new IntPtr(1);
                        break;
                    case WM_RBUTTONUP:
                        MouseButtonEvent?.Invoke(info.x, info.y, 1, false);
                        if (ShouldSuppressMouseButton?.Invoke(1, false) == true)
                            return new IntPtr(1);
                        break;
                    case WM_MBUTTONDOWN:
                        MouseButtonEvent?.Invoke(info.x, info.y, 2, true);
                        if (ShouldSuppressMouseButton?.Invoke(2, true) == true)
                            return new IntPtr(1);
                        break;
                    case WM_MBUTTONUP:
                        MouseButtonEvent?.Invoke(info.x, info.y, 2, false);
                        if (ShouldSuppressMouseButton?.Invoke(2, false) == true)
                            return new IntPtr(1);
                        break;
                    case WM_XBUTTONDOWN:
                        {
                            int xButton = ((int)(info.mouseData >> 16) & 0xFFFF) == 1 ? 3 : 4; // 3=XButton1(侧键后), 4=XButton2(侧键前)
                            MouseButtonEvent?.Invoke(info.x, info.y, xButton, true);
                            if (ShouldSuppressMouseButton?.Invoke(xButton, true) == true)
                                return new IntPtr(1);
                        }
                        break;
                    case WM_XBUTTONUP:
                        {
                            int xButton = ((int)(info.mouseData >> 16) & 0xFFFF) == 1 ? 3 : 4;
                            MouseButtonEvent?.Invoke(info.x, info.y, xButton, false);
                            if (ShouldSuppressMouseButton?.Invoke(xButton, false) == true)
                                return new IntPtr(1);
                        }
                        break;
                    case WM_MOUSEWHEEL:
                        int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                        MouseWheelEvent?.Invoke(info.x, info.y, delta);
                        break;
                    case WM_MOUSEMOVE:
                        MouseMoveEvent?.Invoke(info.x, info.y);
                        break;
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UninstallKeyboardHook();
            UninstallMouseHook();
            GC.SuppressFinalize(this);
        }

        ~GlobalHookManager() => Dispose();
    }
}
