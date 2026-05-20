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

    public class MacroSequence
    {
        public string Name { get; set; } = "序列 1";
        public List<MacroEvent> Events { get; set; } = new();

        [JsonIgnore]
        public string DisplayInfo
        {
            get
            {
                var totalMs = Events.Sum(e => e.DelayMs);
                var duration = totalMs < 1000 ? $"{totalMs}ms" : $"{totalMs / 1000.0:F1}s";
                return $"{Events.Count} 事件, {duration}";
            }
        }
    }

    public class MacroDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "未命名宏";
        public string TriggerKey { get; set; } = "";
        public int TriggerVirtualKeyCode { get; set; }
        public TriggerType TriggerType { get; set; } = TriggerType.Keyboard;
        public int TriggerMouseButton { get; set; } = -1; // -1=未设置, 0=左键, 1=右键, 2=中键, 3=侧键后(X1), 4=侧键前(X2)

        /// <summary>
        /// 多序列列表（v1.4.0+），触发宏时所有序列并行执行
        /// </summary>
        public List<MacroSequence> Sequences { get; set; } = new();

        /// <summary>
        /// 旧版兼容字段：反序列化时如果有 Events 但无 Sequences，自动迁移到 Sequences[0]
        /// 序列化时不再输出此字段
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<MacroEvent>? Events
        {
            get => null; // 不再序列化
            set
            {
                // 反序列化时迁移旧数据
                if (value != null && value.Count > 0)
                {
                    if (Sequences.Count == 0)
                        Sequences.Add(new MacroSequence());
                    if (Sequences[0].Events.Count == 0)
                        Sequences[0].Events = value;
                }
            }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public bool IsEnabled { get; set; } = true;
        public double PlaybackSpeed { get; set; } = 1.0;
        public int RepeatCount { get; set; } = 1; // 0 = infinite
        public bool RecordMouseMovement { get; set; } = false;

        /// <summary>所有序列的总事件数</summary>
        [JsonIgnore]
        public int TotalEventCount => Sequences.Sum(s => s.Events.Count);

        [JsonIgnore]
        public string DisplayInfo => $"{TotalEventCount} 个事件, {Sequences.Count} 个序列 | 触发键: {(string.IsNullOrEmpty(TriggerKey) ? "未设置" : TriggerKey)}";

        [JsonIgnore]
        public string DurationInfo
        {
            get
            {
                // 并行序列取最长的那个
                var maxMs = Sequences.Count > 0 ? Sequences.Max(s => s.Events.Sum(e => e.DelayMs)) : 0;
                if (maxMs < 1000) return $"{maxMs}ms";
                return $"{maxMs / 1000.0:F1}s";
            }
        }
    }
}
