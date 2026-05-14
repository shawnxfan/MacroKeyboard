using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Input;
using MacroKeyboard.Models;

namespace MacroKeyboard.Services
{
    /// <summary>
    /// 宏录制器：监听键盘/鼠标事件并记录为事件序列
    /// </summary>
    public class MacroRecorder
    {
        private readonly GlobalHookManager _hookManager;
        private readonly List<MacroEvent> _events = new();
        private readonly Stopwatch _stopwatch = new();
        private long _lastTimestamp;
        private bool _isRecording;
        private bool _recordMouse;

        public bool IsRecording => _isRecording;
        public event Action<MacroEvent>? EventRecorded;
        public event Action? RecordingStarted;
        public event Action<List<MacroEvent>>? RecordingStopped;

        public MacroRecorder(GlobalHookManager hookManager)
        {
            _hookManager = hookManager;
        }

        public void StartRecording(bool recordMouse = false)
        {
            if (_isRecording) return;

            _events.Clear();
            _recordMouse = recordMouse;
            _isRecording = true;
            _stopwatch.Restart();
            _lastTimestamp = 0;

            _hookManager.KeyEvent += OnKeyEvent;
            if (_recordMouse)
            {
                _hookManager.MouseButtonEvent += OnMouseButtonEvent;
                _hookManager.MouseMoveEvent += OnMouseMoveEvent;
                _hookManager.MouseWheelEvent += OnMouseWheelEvent;
                if (!_hookManager.IsMouseHooked)
                    _hookManager.InstallMouseHook();
            }
            if (!_hookManager.IsKeyboardHooked)
                _hookManager.InstallKeyboardHook();

            RecordingStarted?.Invoke();
        }

        public List<MacroEvent> StopRecording()
        {
            if (!_isRecording) return new();

            _isRecording = false;
            _stopwatch.Stop();

            _hookManager.KeyEvent -= OnKeyEvent;
            _hookManager.MouseButtonEvent -= OnMouseButtonEvent;
            _hookManager.MouseMoveEvent -= OnMouseMoveEvent;
            _hookManager.MouseWheelEvent -= OnMouseWheelEvent;

            var result = new List<MacroEvent>(_events);
            RecordingStopped?.Invoke(result);
            return result;
        }

        private long GetDelay()
        {
            var current = _stopwatch.ElapsedMilliseconds;
            var delay = current - _lastTimestamp;
            _lastTimestamp = current;
            return delay;
        }

        private void OnKeyEvent(int vkCode, bool isDown)
        {
            if (!_isRecording) return;

            var evt = new MacroEvent
            {
                Type = isDown ? MacroEventType.KeyDown : MacroEventType.KeyUp,
                VirtualKeyCode = vkCode,
                KeyName = KeyInterop.KeyFromVirtualKey(vkCode).ToString(),
                DelayMs = GetDelay()
            };

            _events.Add(evt);
            EventRecorded?.Invoke(evt);
        }

        private void OnMouseButtonEvent(int x, int y, int button, bool isDown)
        {
            if (!_isRecording) return;

            var evt = new MacroEvent
            {
                Type = isDown ? MacroEventType.MouseDown : MacroEventType.MouseUp,
                X = x,
                Y = y,
                MouseButton = button,
                DelayMs = GetDelay()
            };

            _events.Add(evt);
            EventRecorded?.Invoke(evt);
        }

        private void OnMouseMoveEvent(int x, int y)
        {
            if (!_isRecording || !_recordMouse) return;

            // 节流：合并短间隔内的移动事件
            if (_events.Count > 0 && _events[^1].Type == MacroEventType.MouseMove
                && _stopwatch.ElapsedMilliseconds - _lastTimestamp < 16) // ~60fps
                return;

            var evt = new MacroEvent
            {
                Type = MacroEventType.MouseMove,
                X = x,
                Y = y,
                DelayMs = GetDelay()
            };

            _events.Add(evt);
            EventRecorded?.Invoke(evt);
        }

        private void OnMouseWheelEvent(int x, int y, int delta)
        {
            if (!_isRecording) return;

            var evt = new MacroEvent
            {
                Type = MacroEventType.MouseWheel,
                X = x,
                Y = y,
                WheelDelta = delta,
                DelayMs = GetDelay()
            };

            _events.Add(evt);
            EventRecorded?.Invoke(evt);
        }
    }
}
