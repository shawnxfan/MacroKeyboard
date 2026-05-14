using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroKeyboard.Models;
using MacroKeyboard.Services;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace MacroKeyboard
{
    public partial class MainWindow : Window
    {
        private readonly GlobalHookManager _hookManager;
        private readonly MacroRecorder _recorder;
        private readonly MacroPlayer _player;
        private readonly MacroStorage _storage;

        private List<MacroDefinition> _macros;
        private MacroDefinition? _selectedMacro;
        private OverlayWindow? _overlay;
        private Forms.NotifyIcon? _trayIcon;
        private bool _isReallyClosing;

        // 触发键绑定状态
        private bool _isBindingTriggerKey;

        // 全局快捷键 VK 码
        private const int VK_F9 = 0x78;
        private const int VK_F10 = 0x79;
        private const int VK_ESCAPE = 0x1B;

        public MainWindow()
        {
            InitializeComponent();

            _hookManager = new GlobalHookManager();
            _recorder = new MacroRecorder(_hookManager);
            _player = new MacroPlayer();
            _storage = new MacroStorage();

            _macros = _storage.LoadAll();
            MacroList.ItemsSource = _macros;

            // 绑定事件
            _recorder.RecordingStarted += OnRecordingStarted;
            _recorder.RecordingStopped += OnRecordingStopped;
            _recorder.EventRecorded += OnEventRecorded;

            _player.PlaybackStarted += OnPlaybackStarted;
            _player.PlaybackStopped += OnPlaybackStopped;
            _player.PlaybackProgress += OnPlaybackProgress;

            // 全局键盘钩子
            _hookManager.KeyEvent += OnGlobalKeyEvent;
            _hookManager.ShouldSuppressKey = ShouldSuppressKey;
            _hookManager.InstallKeyboardHook();

            UpdateFooter();
            Closed += (_, _) =>
            {
                _trayIcon?.Dispose();
                _overlay?.Close();
                _hookManager.Dispose();
            };

            // 创建浮动提示窗口（Loaded 后设置 Owner）
            _overlay = new OverlayWindow();
            Loaded += (_, _) => _overlay.Owner = this;

            // 初始化系统托盘
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "MacroKeyboard - 运行中",
                Visible = true
            };

            // 双击托盘图标恢复窗口
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

            // 右键菜单
            var contextMenu = new Forms.ContextMenuStrip();
            contextMenu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
            contextMenu.Items.Add("-"); // 分隔线
            contextMenu.Items.Add("退出", null, (_, _) =>
            {
                _isReallyClosing = true;
                Close();
            });
            _trayIcon.ContextMenuStrip = contextMenu;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isReallyClosing)
            {
                // 最小化到托盘而不是关闭
                e.Cancel = true;
                Hide();
                _trayIcon!.ShowBalloonTip(2000, "MacroKeyboard", "已最小化到系统托盘，全局快捷键仍然有效。", Forms.ToolTipIcon.Info);
            }
            base.OnClosing(e);
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        #region 全局键盘事件

        private bool ShouldSuppressKey(int vkCode, bool isDown)
        {
            // 录制/回放中不拦截 F9/F10/Esc（让它们作为控制键通过）
            if (vkCode == VK_F9 || vkCode == VK_F10 || vkCode == VK_ESCAPE)
                return false;

            // 回放中按触发键：拦截（防止传给其他应用）但不丢弃事件
            // OnGlobalKeyEvent 会在 ShouldSuppressKey 之前被调用，所以这里只管拦截
            // 注意：这里需要让所有已绑定的触发键在回放时都被拦截（防止传给前台应用）
            if (_player.IsPlaying)
            {
                // 拦截当前正在回放的宏的触发键
                if (_selectedMacro != null && vkCode == _selectedMacro.TriggerVirtualKeyCode)
                    return true;
                // 也拦截其他宏的触发键（防止回放中意外触发其他宏）
                if (_macros.Any(m => m.IsEnabled && m.TriggerVirtualKeyCode == vkCode && m.TriggerVirtualKeyCode != 0))
                    return true;
            }

            return false;
        }

        private void OnGlobalKeyEvent(int vkCode, bool isDown)
        {
            if (!isDown) return; // 只处理按下事件

            Dispatcher.Invoke(() =>
            {
                // 正在绑定触发键
                if (_isBindingTriggerKey && _selectedMacro != null)
                {
                    BindTriggerKey(vkCode);
                    return;
                }

                // F9: 录制/停止
                if (vkCode == VK_F9)
                {
                    HandleRecordToggle();
                    return;
                }

                // F10: 回放/停止切换
                if (vkCode == VK_F10)
                {
                    if (_player.IsPlaying)
                        _player.Stop();
                    else
                        HandlePlayback();
                    return;
                }

                // Esc: 紧急停止
                if (vkCode == VK_ESCAPE)
                {
                    if (_player.IsPlaying)
                    {
                        _player.Stop();
                        return;
                    }
                    if (_recorder.IsRecording)
                    {
                        StopRecording();
                        return;
                    }
                }

                // 检查是否匹配某个宏的触发键
                if (!_recorder.IsRecording)
                {
                    var macro = _macros.FirstOrDefault(m => m.IsEnabled && m.TriggerVirtualKeyCode == vkCode && m.TriggerVirtualKeyCode != 0);
                    if (macro != null)
                    {
                        if (_player.IsPlaying)
                        {
                            // 回放中按任意宏触发键 → 停止回放
                            _player.Stop();
                        }
                        else
                        {
                            // 未回放 → 启动对应宏
                            _selectedMacro = macro;
                            _ = _player.PlayAsync(macro);
                        }
                    }
                }
            });
        }

        #endregion

        #region 录制

        private void HandleRecordToggle()
        {
            if (_recorder.IsRecording)
            {
                StopRecording();
            }
            else if (_selectedMacro != null && !_player.IsPlaying)
            {
                _recorder.StartRecording(_selectedMacro.RecordMouseMovement);
            }
        }

        private void StopRecording()
        {
            var events = _recorder.StopRecording();
            if (_selectedMacro != null && events.Count > 0)
            {
                _selectedMacro.Events = events;
                _selectedMacro.UpdatedAt = DateTime.Now;
                _storage.Save(_selectedMacro, _macros);
                RefreshMacroList();
                UpdateEventList();
            }
        }

        private void OnRecordingStarted()
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus("录制中...", true);
                RecordButton.Content = "⏹ 停止录制 (F9)";
                RecordButton.Style = (Style)FindResource("AccentButton");
                PlayButton.IsEnabled = false;
                EventList.Items.Clear();
                EventCountText.Text = "录制中...";
                DurationText.Text = "";

                // 显示浮动提示
                var name = _selectedMacro?.Name ?? "宏";
                _overlay?.ShowRecording(name);
            });
        }

        private void OnRecordingStopped(List<MacroEvent> events)
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus("就绪", false);
                RecordButton.Content = "⏺ 开始录制 (F9)";
                RecordButton.Style = (Style)FindResource("DangerButton");
                PlayButton.IsEnabled = true;
                UpdateEventList();

                // 隐藏浮动提示
                _overlay?.HideOverlay();
            });
        }

        private void OnEventRecorded(MacroEvent evt)
        {
            Dispatcher.Invoke(() =>
            {
                var text = FormatEvent(evt);
                EventList.Items.Add(text);
                EventList.ScrollIntoView(text);
                EventCountText.Text = $"录制中... ({EventList.Items.Count} 个事件)";

                _overlay?.UpdateRecordingCount(EventList.Items.Count);
            });
        }

        #endregion

        #region 回放

        private void HandlePlayback()
        {
            if (_selectedMacro == null || _selectedMacro.Events.Count == 0) return;
            if (_recorder.IsRecording || _player.IsPlaying) return;

            _ = _player.PlayAsync(_selectedMacro);
        }

        private void OnPlaybackStarted()
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus("回放中...", true);
                PlayButton.Visibility = Visibility.Collapsed;
                RecordButton.IsEnabled = false;
                StopButton.Visibility = Visibility.Visible;

                // 显示浮动提示
                var name = _selectedMacro?.Name ?? "宏";
                _overlay?.ShowPlayback(name);
            });
        }

        private void OnPlaybackStopped()
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus("就绪", false);
                PlayButton.Visibility = Visibility.Visible;
                RecordButton.IsEnabled = true;
                StopButton.Visibility = Visibility.Collapsed;

                // 隐藏浮动提示
                _overlay?.HideOverlay();
            });
        }

        private void OnPlaybackProgress(int current, int total)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"回放中... {current}/{total}";
                _overlay?.UpdateProgress(current, total);
            });
        }

        #endregion

        #region 触发键绑定

        private void BindTriggerKey(int vkCode)
        {
            if (_selectedMacro == null) return;

            // 检查冲突（F9/F10/Esc 保留）
            if (vkCode == VK_F9 || vkCode == VK_F10 || vkCode == VK_ESCAPE)
            {
                MessageBox.Show("F9、F10、Esc 为系统保留键，不能作为触发键。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查是否与其他宏冲突
            var conflict = _macros.FirstOrDefault(m => m.Id != _selectedMacro.Id && m.TriggerVirtualKeyCode == vkCode);
            if (conflict != null)
            {
                MessageBox.Show($"此按键已绑定给宏「{conflict.Name}」，请选择其他按键。", "冲突",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var keyName = KeyInterop.KeyFromVirtualKey(vkCode).ToString();
            _selectedMacro.TriggerKey = keyName;
            _selectedMacro.TriggerVirtualKeyCode = vkCode;
            _selectedMacro.UpdatedAt = DateTime.Now;
            _storage.Save(_selectedMacro, _macros);

            TriggerKeyBox.Text = keyName;
            _isBindingTriggerKey = false;
            TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
            RefreshMacroList();
        }

        #endregion

        #region UI 事件

        private void MacroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedMacro = MacroList.SelectedItem as MacroDefinition;
            var hasSelection = _selectedMacro != null;

            PlaceholderText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            EditPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            EventListPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            ActionPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            DeleteButton.IsEnabled = hasSelection;

            if (_selectedMacro != null)
            {
                MacroNameBox.Text = _selectedMacro.Name;
                TriggerKeyBox.Text = string.IsNullOrEmpty(_selectedMacro.TriggerKey) ? "点击此处设置..." : _selectedMacro.TriggerKey;
                RepeatBox.Text = _selectedMacro.RepeatCount.ToString();
                RecordMouseCheck.IsChecked = _selectedMacro.RecordMouseMovement;

                // 设置速度下拉框
                var speedTag = _selectedMacro.PlaybackSpeed.ToString();
                foreach (ComboBoxItem item in SpeedCombo.Items)
                {
                    if (item.Tag?.ToString() == speedTag)
                    {
                        SpeedCombo.SelectedItem = item;
                        break;
                    }
                }

                UpdateEventList();
            }
        }

        private void NewMacro_Click(object sender, RoutedEventArgs e)
        {
            var macro = new MacroDefinition
            {
                Name = $"宏 {_macros.Count + 1}"
            };
            _macros.Add(macro);
            _storage.SaveAll(_macros);
            RefreshMacroList();
            MacroList.SelectedItem = macro;
        }

        private void DeleteMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null) return;

            var result = MessageBox.Show($"确定删除宏「{_selectedMacro.Name}」？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _storage.Delete(_selectedMacro.Id, _macros);
                _selectedMacro = null;
                RefreshMacroList();
            }
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            HandleRecordToggle();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            HandlePlayback();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_player.IsPlaying)
                _player.Stop();
        }

        private void MacroNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedMacro == null) return;
            _selectedMacro.Name = MacroNameBox.Text;
            _selectedMacro.UpdatedAt = DateTime.Now;
            _storage.Save(_selectedMacro, _macros);
            RefreshMacroList();
        }

        private void TriggerKeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _isBindingTriggerKey = true;
            TriggerKeyBox.Text = "按下目标按键...";
            TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x1C));
        }

        private void TriggerKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _isBindingTriggerKey = false;
            TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
            if (_selectedMacro != null)
                TriggerKeyBox.Text = string.IsNullOrEmpty(_selectedMacro.TriggerKey) ? "点击此处设置..." : _selectedMacro.TriggerKey;
        }

        private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedMacro == null || SpeedCombo.SelectedItem is not ComboBoxItem item) return;

            if (double.TryParse(item.Tag?.ToString(), out double speed))
            {
                // 0 表示"最快" → 设一个极大值
                _selectedMacro.PlaybackSpeed = speed == 0 ? 10000 : speed;
                _selectedMacro.UpdatedAt = DateTime.Now;
                _storage.Save(_selectedMacro, _macros);
            }
        }

        private void RepeatBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedMacro == null) return;
            if (int.TryParse(RepeatBox.Text, out int count) && count >= 0)
            {
                _selectedMacro.RepeatCount = count;
                _selectedMacro.UpdatedAt = DateTime.Now;
                _storage.Save(_selectedMacro, _macros);
            }
        }

        private void RecordMouseCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null) return;
            _selectedMacro.RecordMouseMovement = RecordMouseCheck.IsChecked == true;
            _selectedMacro.UpdatedAt = DateTime.Now;
            _storage.Save(_selectedMacro, _macros);
        }

        #endregion

        #region 辅助方法

        private void RefreshMacroList()
        {
            var selected = _selectedMacro;
            MacroList.ItemsSource = null;
            MacroList.ItemsSource = _macros;
            if (selected != null)
                MacroList.SelectedItem = _macros.FirstOrDefault(m => m.Id == selected.Id);
        }

        private void UpdateEventList()
        {
            EventList.Items.Clear();
            if (_selectedMacro == null) return;

            foreach (var evt in _selectedMacro.Events)
            {
                EventList.Items.Add(FormatEvent(evt));
            }

            EventCountText.Text = $"事件列表（{_selectedMacro.Events.Count} 个事件）";
            DurationText.Text = _selectedMacro.Events.Count > 0 ? $"总时长: {_selectedMacro.DurationInfo}" : "";
        }

        private void SetStatus(string text, bool isActive)
        {
            StatusText.Text = text;
            StatusDot.Fill = isActive
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0x4D, 0x4D))
                : new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        }

        private void UpdateFooter()
        {
            FooterRight.Text = $"存储: {_storage.StorageDirectory}";
        }

        private static string FormatEvent(MacroEvent evt)
        {
            var delay = evt.DelayMs > 0 ? $"+{evt.DelayMs}ms " : "";
            return evt.Type switch
            {
                MacroEventType.KeyDown => $"{delay}⬇ 按下 {evt.KeyName} (0x{evt.VirtualKeyCode:X2})",
                MacroEventType.KeyUp => $"{delay}⬆ 释放 {evt.KeyName} (0x{evt.VirtualKeyCode:X2})",
                MacroEventType.MouseDown => $"{delay}🖱⬇ 鼠标{ButtonName(evt.MouseButton)}按下 ({evt.X},{evt.Y})",
                MacroEventType.MouseUp => $"{delay}🖱⬆ 鼠标{ButtonName(evt.MouseButton)}释放 ({evt.X},{evt.Y})",
                MacroEventType.MouseMove => $"{delay}🖱→ 移动到 ({evt.X},{evt.Y})",
                MacroEventType.MouseWheel => $"{delay}🖱↕ 滚轮 {evt.WheelDelta} ({evt.X},{evt.Y})",
                _ => $"{delay}{evt.Type}"
            };
        }

        private static string ButtonName(int button) => button switch
        {
            0 => "左键",
            1 => "右键",
            2 => "中键",
            _ => $"按钮{button}"
        };

        #endregion
    }
}
