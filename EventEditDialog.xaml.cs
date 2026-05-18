using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroKeyboard.Models;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace MacroKeyboard
{
    public partial class EventEditDialog : Window
    {
        public MacroEvent? ResultEvent { get; private set; }
        public bool InsertKeyPress { get; private set; } // 如果选择"按下+释放"，返回两个事件

        private int _capturedVkCode;
        private string _capturedKeyName = "";
        private bool _isCapturing;

        // 编辑模式
        public bool IsEditMode { get; set; }

        public EventEditDialog(MacroEvent? existingEvent = null)
        {
            InitializeComponent();

            if (existingEvent != null)
            {
                IsEditMode = true;
                Title = "编辑事件";
                LoadExistingEvent(existingEvent);
            }
        }

        private void LoadExistingEvent(MacroEvent evt)
        {
            // 设置事件类型
            switch (evt.Type)
            {
                case MacroEventType.KeyDown:
                    EventTypeCombo.SelectedIndex = 0;
                    break;
                case MacroEventType.KeyUp:
                    EventTypeCombo.SelectedIndex = 1;
                    break;
                case MacroEventType.Delay:
                    EventTypeCombo.SelectedIndex = 3;
                    break;
                default:
                    EventTypeCombo.SelectedIndex = 0;
                    break;
            }

            // 设置按键
            if (evt.Type != MacroEventType.Delay)
            {
                _capturedVkCode = evt.VirtualKeyCode;
                _capturedKeyName = evt.KeyName ?? "";
                KeyInputBox.Text = _capturedKeyName;
            }

            // 设置延迟
            DelayInputBox.Text = evt.DelayMs.ToString();
        }

        private void EventTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EventTypeCombo.SelectedItem is not ComboBoxItem item) return;

            var tag = item.Tag?.ToString();
            bool isDelay = tag == "Delay";

            // 延迟类型不需要按键输入
            if (KeyInputBox != null)
            {
                KeyInputBox.IsEnabled = !isDelay;
                KeyInputBox.Opacity = isDelay ? 0.4 : 1.0;
            }

            if (DelayLabel != null && isDelay)
            {
                DelayLabel.Text = "延迟时间 (毫秒)";
            }
            else if (DelayLabel != null)
            {
                DelayLabel.Text = "前置延迟 (毫秒)";
            }
        }

        private void KeyInputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _isCapturing = true;
            KeyInputBox.Text = "按下目标按键...";
            KeyInputBox.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x1C));
        }

        private void KeyInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _isCapturing = false;
            KeyInputBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
            if (string.IsNullOrEmpty(_capturedKeyName))
                KeyInputBox.Text = "点击此处后按下按键...";
            else
                KeyInputBox.Text = _capturedKeyName;
        }

        private void KeyInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!_isCapturing) return;

            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vkCode = KeyInterop.VirtualKeyFromKey(key);

            if (vkCode == 0) return;

            _capturedVkCode = vkCode;
            _capturedKeyName = key.ToString();
            KeyInputBox.Text = _capturedKeyName;

            // 自动移走焦点
            DelayInputBox.Focus();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            var selectedType = (EventTypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            if (!long.TryParse(DelayInputBox.Text, out long delayMs) || delayMs < 0)
            {
                MessageBox.Show("请输入有效的延迟时间（非负整数）。", "输入错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (selectedType == "Delay")
            {
                if (delayMs == 0)
                {
                    MessageBox.Show("延迟事件的时间必须大于 0。", "输入错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ResultEvent = new MacroEvent
                {
                    Type = MacroEventType.Delay,
                    DelayMs = delayMs
                };
            }
            else if (selectedType == "KeyPress")
            {
                if (_capturedVkCode == 0)
                {
                    MessageBox.Show("请先按下一个目标按键。", "输入错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // "按下+释放" 标记
                InsertKeyPress = true;
                ResultEvent = new MacroEvent
                {
                    Type = MacroEventType.KeyDown,
                    VirtualKeyCode = _capturedVkCode,
                    KeyName = _capturedKeyName,
                    DelayMs = delayMs
                };
            }
            else
            {
                if (_capturedVkCode == 0)
                {
                    MessageBox.Show("请先按下一个目标按键。", "输入错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var type = selectedType == "KeyUp" ? MacroEventType.KeyUp : MacroEventType.KeyDown;
                ResultEvent = new MacroEvent
                {
                    Type = type,
                    VirtualKeyCode = _capturedVkCode,
                    KeyName = _capturedKeyName,
                    DelayMs = delayMs
                };
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
