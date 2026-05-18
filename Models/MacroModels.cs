using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MacroKeyboard.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum MacroEventType
    {
        KeyDown,
        KeyUp,
        MouseMove,
        MouseDown,
        MouseUp,
        MouseWheel,
        Delay
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum TriggerType
    {
        Keyboard,
        Mouse
    }

    public class MacroEvent
    {
        public MacroEventType Type { get; set; }

        // 键盘事件
        public int VirtualKeyCode { get; set; }
        public string? KeyName { get; set; }

        // 鼠标事件
        public int X { get; set; }
        public int Y { get; set; }
        public int MouseButton { get; set; } // 0=Left, 1=Right, 2=Middle
        public int WheelDelta { get; set; }

        // 时间间隔（毫秒）
        public long DelayMs { get; set; }
    }

    public class MacroDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "未命名宏";
        public string TriggerKey { get; set; } = "";
        public int TriggerVirtualKeyCode { get; set; }
        public TriggerType TriggerType { get; set; } = TriggerType.Keyboard;
        public int TriggerMouseButton { get; set; } = -1; // -1=未设置, 0=左键, 1=右键, 2=中键, 3=侧键后(X1), 4=侧键前(X2)
        public List<MacroEvent> Events { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public bool IsEnabled { get; set; } = true;
        public double PlaybackSpeed { get; set; } = 1.0;
        public int RepeatCount { get; set; } = 1; // 0 = infinite
        public bool RecordMouseMovement { get; set; } = false;

        [JsonIgnore]
        public string DisplayInfo => $"{Events.Count} 个事件 | 触发键: {(string.IsNullOrEmpty(TriggerKey) ? "未设置" : TriggerKey)}";

        [JsonIgnore]
        public string DurationInfo
        {
            get
            {
                var totalMs = Events.Sum(e => e.DelayMs);
                if (totalMs < 1000) return $"{totalMs}ms";
                return $"{totalMs / 1000.0:F1}s";
            }
        }
    }
}
