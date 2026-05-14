using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Windows.Media.Color;

namespace MacroKeyboard
{
    public partial class OverlayWindow : Window
    {
        public OverlayWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 显示回放中状态
        /// </summary>
        public void ShowPlayback(string macroName)
        {
            MacroNameText.Text = macroName;
            StatusLabel.Text = "▶ 回放中";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)); // 橙色
            Indicator.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
            ProgressText.Text = "";
            StartPulseAnimation();
            Show();
        }

        /// <summary>
        /// 显示录制中状态
        /// </summary>
        public void ShowRecording(string macroName)
        {
            MacroNameText.Text = macroName;
            StatusLabel.Text = "⏺ 录制中";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x4D, 0x4D)); // 红色
            Indicator.Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x4D, 0x4D));
            ProgressText.Text = "";
            StartPulseAnimation();
            Show();
        }

        /// <summary>
        /// 更新进度
        /// </summary>
        public void UpdateProgress(int current, int total)
        {
            ProgressText.Text = $"({current}/{total})";
        }

        /// <summary>
        /// 更新录制事件数
        /// </summary>
        public void UpdateRecordingCount(int count)
        {
            ProgressText.Text = $"({count} 事件)";
        }

        /// <summary>
        /// 隐藏并停止动画
        /// </summary>
        public void HideOverlay()
        {
            Indicator.BeginAnimation(OpacityProperty, null);
            Hide();
        }

        private void StartPulseAnimation()
        {
            var animation = new DoubleAnimation
            {
                From = 1.0,
                To = 0.3,
                Duration = TimeSpan.FromMilliseconds(600),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            Indicator.BeginAnimation(OpacityProperty, animation);
        }
    }
}
