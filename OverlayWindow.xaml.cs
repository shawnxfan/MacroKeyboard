using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;

namespace MacroKeyboard
{
    public partial class OverlayWindow : Window
    {
        /// <summary>每个活跃条目的 UI 元素</summary>
        private class OverlayEntry
        {
            public StackPanel Panel { get; set; } = null!;
            public Ellipse Indicator { get; set; } = null!;
            public TextBlock NameText { get; set; } = null!;
            public TextBlock StatusText { get; set; } = null!;
            public TextBlock ProgressText { get; set; } = null!;
        }

        private readonly Dictionary<string, OverlayEntry> _entries = new();

        public OverlayWindow()
        {
            InitializeComponent();
        }

        /// <summary>添加一个回放中的宏条目</summary>
        public void ShowPlayback(string id, string macroName)
        {
            if (_entries.ContainsKey(id)) return;
            AddEntry(id, macroName, "▶ 回放中", Color.FromRgb(0xFF, 0x98, 0x00));
        }

        /// <summary>添加一个录制中的宏条目</summary>
        public void ShowRecording(string id, string macroName)
        {
            if (_entries.ContainsKey(id)) return;
            AddEntry(id, macroName, "⏺ 录制中", Color.FromRgb(0xE8, 0x4D, 0x4D));
        }

        /// <summary>更新回放进度</summary>
        public void UpdateProgress(string id, int current, int total)
        {
            if (_entries.TryGetValue(id, out var entry))
                entry.ProgressText.Text = $"({current}/{total})";
        }

        /// <summary>更新录制事件数</summary>
        public void UpdateRecordingCount(string id, int count)
        {
            if (_entries.TryGetValue(id, out var entry))
                entry.ProgressText.Text = $"({count} 事件)";
        }

        /// <summary>移除指定条目，如果没有条目了则隐藏窗口</summary>
        public void RemoveEntry(string id)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Indicator.BeginAnimation(OpacityProperty, null);
                ItemsPanel.Children.Remove(entry.Panel);
                _entries.Remove(id);
            }

            if (_entries.Count == 0)
                Hide();
        }

        /// <summary>移除所有条目并隐藏</summary>
        public void HideOverlay()
        {
            foreach (var entry in _entries.Values)
            {
                entry.Indicator.BeginAnimation(OpacityProperty, null);
            }
            _entries.Clear();
            ItemsPanel.Children.Clear();
            Hide();
        }

        private void AddEntry(string id, string name, string status, Color color)
        {
            var indicator = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text = name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                FontSize = 13, FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var statusText = new TextBlock
            {
                Text = status,
                Foreground = new SolidColorBrush(color),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var progressText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA0)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };
            row.Children.Add(indicator);
            row.Children.Add(nameText);
            row.Children.Add(statusText);
            row.Children.Add(progressText);

            ItemsPanel.Children.Add(row);

            var entry = new OverlayEntry
            {
                Panel = row,
                Indicator = indicator,
                NameText = nameText,
                StatusText = statusText,
                ProgressText = progressText
            };
            _entries[id] = entry;

            // 呼吸灯动画
            var animation = new DoubleAnimation
            {
                From = 1.0, To = 0.3,
                Duration = TimeSpan.FromMilliseconds(600),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            indicator.BeginAnimation(OpacityProperty, animation);

            Show();
        }
    }
}
