using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MacroKeyboard.Models;

namespace MacroKeyboard.Services
{
    /// <summary>
    /// 宏回放器：通过 SendInput 模拟键盘/鼠标事件
    /// </summary>
    public class MacroPlayer
    {
        #region Win32 API

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        #endregion

        private CancellationTokenSource? _cts;
        private bool _isPlaying;

        public bool IsPlaying => _isPlaying;
        public event Action? PlaybackStarted;
        public event Action? PlaybackStopped;
        public event Action<int, int>? PlaybackProgress; // (current, total)

        public async Task PlayAsync(MacroDefinition macro)
        {
            if (_isPlaying || macro.Events.Count == 0) return;

            _isPlaying = true;
            _cts = new CancellationTokenSource();
            PlaybackStarted?.Invoke();

            try
            {
                int repeatCount = macro.RepeatCount <= 0 ? int.MaxValue : macro.RepeatCount;

                for (int repeat = 0; repeat < repeatCount; repeat++)
                {
                    for (int i = 0; i < macro.Events.Count; i++)
                    {
                        _cts.Token.ThrowIfCancellationRequested();

                        var evt = macro.Events[i];

                        // 延迟（按回放速度调整）
                        if (evt.DelayMs > 0)
                        {
                            int delay = (int)(evt.DelayMs / macro.PlaybackSpeed);
                            if (delay > 0)
                                await Task.Delay(delay, _cts.Token);
                        }

                        ExecuteEvent(evt);
                        PlaybackProgress?.Invoke(i + 1, macro.Events.Count);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _isPlaying = false;
                _cts?.Dispose();
                _cts = null;
                PlaybackStopped?.Invoke();
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private void ExecuteEvent(MacroEvent evt)
        {
            switch (evt.Type)
            {
                case MacroEventType.KeyDown:
                    SendKeyInput((ushort)evt.VirtualKeyCode, KEYEVENTF_KEYDOWN);
                    break;
                case MacroEventType.KeyUp:
                    SendKeyInput((ushort)evt.VirtualKeyCode, KEYEVENTF_KEYUP);
                    break;
                case MacroEventType.MouseDown:
                    SendMouseButton(evt.X, evt.Y, evt.MouseButton, true);
                    break;
                case MacroEventType.MouseUp:
                    SendMouseButton(evt.X, evt.Y, evt.MouseButton, false);
                    break;
                case MacroEventType.MouseMove:
                    SendMouseMove(evt.X, evt.Y);
                    break;
                case MacroEventType.MouseWheel:
                    SendMouseWheel(evt.X, evt.Y, evt.WheelDelta);
                    break;
            }
        }

        private void SendKeyInput(ushort vk, uint flags)
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = flags,
                        dwExtraInfo = GlobalHookManager.INJECTED_FLAG
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private void SendMouseMove(int x, int y)
        {
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = (int)((x * 65535.0) / screenW),
                        dy = (int)((y * 65535.0) / screenH),
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                        dwExtraInfo = GlobalHookManager.INJECTED_FLAG
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private void SendMouseButton(int x, int y, int button, bool isDown)
        {
            SendMouseMove(x, y);

            uint flags = (button, isDown) switch
            {
                (0, true) => MOUSEEVENTF_LEFTDOWN,
                (0, false) => MOUSEEVENTF_LEFTUP,
                (1, true) => MOUSEEVENTF_RIGHTDOWN,
                (1, false) => MOUSEEVENTF_RIGHTUP,
                (2, true) => MOUSEEVENTF_MIDDLEDOWN,
                (2, false) => MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = flags,
                        dwExtraInfo = GlobalHookManager.INJECTED_FLAG
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private void SendMouseWheel(int x, int y, int delta)
        {
            SendMouseMove(x, y);

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = delta,
                        dwFlags = MOUSEEVENTF_WHEEL,
                        dwExtraInfo = GlobalHookManager.INJECTED_FLAG
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
    }
}
