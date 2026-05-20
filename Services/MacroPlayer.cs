using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MacroKeyboard.Models;

namespace MacroKeyboard.Services
{
    /// <summary>
    /// 宏回放器：支持多个宏同时并发回放，每个宏独立控制
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

        /// <summary>
        /// 跟踪每个正在回放的宏的 CancellationTokenSource
        /// Key = MacroDefinition.Id
        /// </summary>
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeMacros = new();

        /// <summary>
        /// 跟踪每个宏当前按下但尚未释放的按键（VK 码）
        /// </summary>
        private readonly ConcurrentDictionary<string, HashSet<ushort>> _pressedKeys = new();

        /// <summary>
        /// 跟踪每个宏当前按下但尚未释放的鼠标按钮
        /// </summary>
        private readonly ConcurrentDictionary<string, HashSet<int>> _pressedMouseButtons = new();

        /// <summary>任意宏正在回放</summary>
        public bool IsPlaying => !_activeMacros.IsEmpty;

        /// <summary>指定宏是否正在回放</summary>
        public bool IsMacroPlaying(string macroId) => _activeMacros.ContainsKey(macroId);

        /// <summary>当前正在回放的宏 ID 列表</summary>
        public ICollection<string> ActiveMacroIds => _activeMacros.Keys;

        /// <summary>当前活跃宏数量</summary>
        public int ActiveCount => _activeMacros.Count;

        // 事件：携带 macroId 以区分是哪个宏
        public event Action<string>? PlaybackStarted;       // macroId
        public event Action<string>? PlaybackStopped;       // macroId
        public event Action<string, int, int>? PlaybackProgress; // macroId, current, total

        /// <summary>
        /// 启动指定宏的回放（不阻塞其他宏）
        /// </summary>
        public async Task PlayAsync(MacroDefinition macro)
        {
            if (macro.Sequences.Count == 0 || macro.Sequences.All(s => s.Events.Count == 0)) return;

            // 如果该宏已在回放，不重复启动
            if (_activeMacros.ContainsKey(macro.Id)) return;

            var cts = new CancellationTokenSource();
            if (!_activeMacros.TryAdd(macro.Id, cts))
            {
                cts.Dispose();
                return;
            }

            // 初始化按键跟踪
            _pressedKeys[macro.Id] = new HashSet<ushort>();
            _pressedMouseButtons[macro.Id] = new HashSet<int>();

            PlaybackStarted?.Invoke(macro.Id);

            try
            {
                int repeatCount = macro.RepeatCount <= 0 ? int.MaxValue : macro.RepeatCount;

                for (int repeat = 0; repeat < repeatCount; repeat++)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // 所有序列并行执行
                    var tasks = macro.Sequences
                        .Where(s => s.Events.Count > 0)
                        .Select(seq => PlaySequenceAsync(seq, macro.Id, macro.PlaybackSpeed, cts.Token))
                        .ToArray();

                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // 释放所有残留的按键和鼠标按钮
                ReleaseAllPressedKeys(macro.Id);

                _activeMacros.TryRemove(macro.Id, out _);
                _pressedKeys.TryRemove(macro.Id, out _);
                _pressedMouseButtons.TryRemove(macro.Id, out _);
                cts.Dispose();
                PlaybackStopped?.Invoke(macro.Id);
            }
        }

        /// <summary>
        /// 播放单个序列（内部方法，由 PlayAsync 并行调用）
        /// </summary>
        private async Task PlaySequenceAsync(MacroSequence sequence, string macroId, double playbackSpeed, CancellationToken token)
        {
            for (int i = 0; i < sequence.Events.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var evt = sequence.Events[i];

                // 延迟（按回放速度调整）
                if (evt.DelayMs > 0)
                {
                    int delay = (int)(evt.DelayMs / playbackSpeed);
                    if (delay > 0)
                        await Task.Delay(delay, token);
                }

                ExecuteEvent(evt, macroId);
            }
        }

        /// <summary>停止指定宏的回放</summary>
        public void Stop(string macroId)
        {
            if (_activeMacros.TryGetValue(macroId, out var cts))
                cts.Cancel();
        }

        /// <summary>停止所有正在回放的宏</summary>
        public void StopAll()
        {
            foreach (var kvp in _activeMacros)
                kvp.Value.Cancel();
        }

        private void ExecuteEvent(MacroEvent evt, string macroId)
        {
            switch (evt.Type)
            {
                case MacroEventType.KeyDown:
                    if (_pressedKeys.TryGetValue(macroId, out var keys))
                        keys.Add((ushort)evt.VirtualKeyCode);
                    SendKeyInput((ushort)evt.VirtualKeyCode, KEYEVENTF_KEYDOWN);
                    break;
                case MacroEventType.KeyUp:
                    if (_pressedKeys.TryGetValue(macroId, out var keysUp))
                        keysUp.Remove((ushort)evt.VirtualKeyCode);
                    SendKeyInput((ushort)evt.VirtualKeyCode, KEYEVENTF_KEYUP);
                    break;
                case MacroEventType.MouseDown:
                    if (_pressedMouseButtons.TryGetValue(macroId, out var btns))
                        btns.Add(evt.MouseButton);
                    SendMouseButton(evt.X, evt.Y, evt.MouseButton, true);
                    break;
                case MacroEventType.MouseUp:
                    if (_pressedMouseButtons.TryGetValue(macroId, out var btnsUp))
                        btnsUp.Remove(evt.MouseButton);
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

        /// <summary>
        /// 释放指定宏所有残留的按键和鼠标按钮
        /// </summary>
        private void ReleaseAllPressedKeys(string macroId)
        {
            // 释放所有残留的键盘按键
            if (_pressedKeys.TryGetValue(macroId, out var keys))
            {
                foreach (var vk in keys)
                {
                    SendKeyInput(vk, KEYEVENTF_KEYUP);
                }
                keys.Clear();
            }

            // 释放所有残留的鼠标按钮
            if (_pressedMouseButtons.TryGetValue(macroId, out var buttons))
            {
                foreach (var btn in buttons)
                {
                    // 使用当前鼠标位置释放
                    SendMouseButtonRelease(btn);
                }
                buttons.Clear();
            }
        }

        /// <summary>
        /// 释放鼠标按钮（不移动鼠标位置）
        /// </summary>
        private void SendMouseButtonRelease(int button)
        {
            uint flags = button switch
            {
                0 => MOUSEEVENTF_LEFTUP,
                1 => MOUSEEVENTF_RIGHTUP,
                2 => MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };
            if (flags == 0) return;

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
