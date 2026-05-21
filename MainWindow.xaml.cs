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
        private int _currentSequenceIndex; // 当前选中的序列索引
        private OverlayWindow? _overlay;
        private Forms.NotifyIcon? _trayIcon;
        private bool _isReallyClosing;
        private bool _isRefreshingMacroList; // 防止刷新列表时重置序列索引

        // 触发键绑定状态
        private bool _isBindingTriggerKey;

        // 宏触发全局开关
        private bool _isMacroTriggerEnabled = true;

        // 全局快捷键 VK 码
        private const int VK_F9 = 0x78;
        private const int VK_F10 = 0x79;
        private const int VK_ESCAPE = 0x1B;

        // 录制用的固定 ID（Overlay 用）
        private const string RECORDING_OVERLAY_ID = "__recording__";

        /// <summary>获取当前选中宏的当前选中序列</summary>
        private MacroSequence? CurrentSequence =>
            _selectedMacro != null && _currentSequenceIndex < _selectedMacro.Sequences.Count
                ? _selectedMacro.Sequences[_currentSequenceIndex]
                : null;

        /// <summary>获取当前序列的事件列表（简写）</summary>
        private List<MacroEvent>? CurrentEvents => CurrentSequence?.Events;

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

            // 全局鼠标钩子（用于鼠标触发键）
            _hookManager.MouseButtonEvent += OnGlobalMouseButtonEvent;
            _hookManager.ShouldSuppressMouseButton = ShouldSuppressMouseButton;
            _hookManager.InstallMouseHook();

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
            // 从嵌入资源加载图标
            System.Drawing.Icon appIcon;
            try
            {
                var resourceUri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
                var streamInfo = System.Windows.Application.GetResourceStream(resourceUri);
                appIcon = streamInfo != null
                    ? new System.Drawing.Icon(streamInfo.Stream)
                    : SystemIcons.Application;
            }
            catch
            {
                appIcon = SystemIcons.Application;
            }

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = appIcon,
                Text = "MacroKeyboard - 运行中",
                Visible = true
            };

            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

            var contextMenu = new Forms.ContextMenuStrip();
            contextMenu.Items.Add("显示主窗口", null, (_, _) => ShowMainWindow());
            contextMenu.Items.Add("-");

            var toggleTriggerItem = new Forms.ToolStripMenuItem("宏触发: 已启用");
            toggleTriggerItem.Click += (_, _) =>
            {
                _isMacroTriggerEnabled = !_isMacroTriggerEnabled;
                toggleTriggerItem.Text = _isMacroTriggerEnabled ? "宏触发: 已启用" : "宏触发: 已禁用";
                _trayIcon!.Text = _isMacroTriggerEnabled
                    ? "MacroKeyboard - 运行中"
                    : "MacroKeyboard - 宏触发已禁用";
                Dispatcher.Invoke(() =>
                {
                    TriggerDisabledBadge.Visibility = _isMacroTriggerEnabled
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                });
            };
            contextMenu.Items.Add(toggleTriggerItem);

            contextMenu.Items.Add("-");
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
            // F9/F10/Esc 始终放行（控制键）
            if (vkCode == VK_F9 || vkCode == VK_F10 || vkCode == VK_ESCAPE)
                return false;

            // 宏触发禁用时不拦截
            if (!_isMacroTriggerEnabled)
                return false;

            // 如果有任何宏在回放，拦截所有已绑定的触发键（防止传给前台应用）
            if (_player.IsPlaying)
            {
                if (_macros.Any(m => m.IsEnabled && m.TriggerType == Models.TriggerType.Keyboard && m.TriggerVirtualKeyCode == vkCode && m.TriggerVirtualKeyCode != 0))
                    return true;
            }

            return false;
        }

        private void OnGlobalKeyEvent(int vkCode, bool isDown)
        {
            if (!isDown) return;

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

                // F10: 回放当前选中的宏/停止当前选中的宏
                if (vkCode == VK_F10)
                {
                    if (_selectedMacro != null && _player.IsMacroPlaying(_selectedMacro.Id))
                        _player.Stop(_selectedMacro.Id);
                    else
                        HandlePlayback();
                    return;
                }

                // Esc: 停止所有回放和录制
                if (vkCode == VK_ESCAPE)
                {
                    if (_player.IsPlaying)
                    {
                        _player.StopAll();
                        return;
                    }
                    if (_recorder.IsRecording)
                    {
                        StopRecording();
                        return;
                    }
                }

                // 检查是否匹配某个宏的触发键
                if (!_recorder.IsRecording && _isMacroTriggerEnabled)
                {
                    var macro = _macros.FirstOrDefault(m => m.IsEnabled && m.TriggerType == Models.TriggerType.Keyboard && m.TriggerVirtualKeyCode == vkCode && m.TriggerVirtualKeyCode != 0);
                    if (macro != null)
                    {
                        if (_player.IsMacroPlaying(macro.Id))
                        {
                            // 该宏正在回放 → 停止它
                            _player.Stop(macro.Id);
                        }
                        else
                        {
                            // 该宏未在回放 → 启动它（不影响其他正在回放的宏）
                            _ = _player.PlayAsync(macro);
                        }
                    }
                }
            });
        }

        private bool ShouldSuppressMouseButton(int button, bool isDown)
        {
            // 宏触发禁用时不拦截
            if (!_isMacroTriggerEnabled)
                return false;

            // 如果有任何宏在回放，拦截所有绑定为鼠标触发键的按钮
            if (_player.IsPlaying)
            {
                if (_macros.Any(m => m.IsEnabled && m.TriggerType == Models.TriggerType.Mouse && m.TriggerMouseButton == button))
                    return true;
            }

            return false;
        }

        private void OnGlobalMouseButtonEvent(int x, int y, int button, bool isDown)
        {
            if (!isDown) return;

            Dispatcher.Invoke(() =>
            {
                // 正在绑定触发键 — 鼠标按键也可以作为触发键
                if (_isBindingTriggerKey && _selectedMacro != null)
                {
                    BindTriggerMouseButton(button);
                    return;
                }

                // 检查是否匹配某个宏的鼠标触发键
                if (!_recorder.IsRecording && _isMacroTriggerEnabled)
                {
                    var macro = _macros.FirstOrDefault(m => m.IsEnabled && m.TriggerType == Models.TriggerType.Mouse && m.TriggerMouseButton == button);
                    if (macro != null)
                    {
                        if (_player.IsMacroPlaying(macro.Id))
                        {
                            _player.Stop(macro.Id);
                        }
                        else
                        {
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
            else if (_selectedMacro != null && !_player.IsMacroPlaying(_selectedMacro.Id))
            {
                _recorder.StartRecording(_selectedMacro.RecordMouseMovement);
            }
        }

        private void StopRecording()
        {
            var events = _recorder.StopRecording();
            if (_selectedMacro != null && events.Count > 0 && CurrentSequence != null)
            {
                CurrentSequence.Events = events;
                _selectedMacro.UpdatedAt = DateTime.Now;
                _storage.Save(_selectedMacro, _macros);
                RefreshMacroList();
                UpdateEventList();
                RefreshSequenceTabs();
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

                var name = _selectedMacro?.Name ?? "宏";
                _overlay?.ShowRecording(RECORDING_OVERLAY_ID, name);
            });
        }

        private void OnRecordingStopped(List<MacroEvent> events)
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus(_player.IsPlaying ? $"回放中... ({_player.ActiveCount} 个宏)" : "就绪", _player.IsPlaying);
                RecordButton.Content = "⏺ 开始录制 (F9)";
                RecordButton.Style = (Style)FindResource("DangerButton");
                PlayButton.IsEnabled = true;
                UpdateEventList();

                _overlay?.RemoveEntry(RECORDING_OVERLAY_ID);
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

                _overlay?.UpdateRecordingCount(RECORDING_OVERLAY_ID, EventList.Items.Count);
            });
        }

        #endregion

        #region 回放

        private void HandlePlayback()
        {
            if (_selectedMacro == null || _selectedMacro.TotalEventCount == 0) return;
            if (_recorder.IsRecording) return;
            if (_player.IsMacroPlaying(_selectedMacro.Id)) return;

            _ = _player.PlayAsync(_selectedMacro);
        }

        private void OnPlaybackStarted(string macroId)
        {
            Dispatcher.Invoke(() =>
            {
                var macro = _macros.FirstOrDefault(m => m.Id == macroId);
                var name = macro?.Name ?? "宏";

                SetStatus($"回放中... ({_player.ActiveCount} 个宏)", true);

                // 如果是当前选中的宏，更新按钮状态
                if (_selectedMacro?.Id == macroId)
                {
                    PlayButton.Visibility = Visibility.Collapsed;
                    RecordButton.IsEnabled = false;
                    StopButton.Visibility = Visibility.Visible;
                }

                _overlay?.ShowPlayback(macroId, name);
            });
        }

        private void OnPlaybackStopped(string macroId)
        {
            Dispatcher.Invoke(() =>
            {
                _overlay?.RemoveEntry(macroId);

                if (_player.IsPlaying)
                {
                    SetStatus($"回放中... ({_player.ActiveCount} 个宏)", true);
                }
                else
                {
                    SetStatus("就绪", false);
                }

                // 如果是当前选中的宏，恢复按钮状态
                if (_selectedMacro?.Id == macroId)
                {
                    PlayButton.Visibility = Visibility.Visible;
                    RecordButton.IsEnabled = !_player.IsPlaying || true; // 录制不受其他宏回放影响
                    StopButton.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void OnPlaybackProgress(string macroId, int current, int total)
        {
            Dispatcher.Invoke(() =>
            {
                if (_selectedMacro?.Id == macroId)
                    StatusText.Text = $"回放中... {current}/{total} ({_player.ActiveCount} 个宏)";

                _overlay?.UpdateProgress(macroId, current, total);
            });
        }

        #endregion

        #region 触发键绑定

        private void BindTriggerKey(int vkCode)
        {
            if (_selectedMacro == null) return;

            if (vkCode == VK_F9 || vkCode == VK_F10 || vkCode == VK_ESCAPE)
            {
                _isBindingTriggerKey = false;
                TriggerKeyBox.Text = string.IsNullOrEmpty(_selectedMacro.TriggerKey) ? "点击此处设置..." : _selectedMacro.TriggerKey;
                TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
                MessageBox.Show("F9、F10、Esc 为系统保留键，不能作为触发键。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var conflict = _macros.FirstOrDefault(m => m.Id != _selectedMacro.Id && m.TriggerType == Models.TriggerType.Keyboard && m.TriggerVirtualKeyCode == vkCode);
            if (conflict != null)
            {
                _isBindingTriggerKey = false;
                TriggerKeyBox.Text = string.IsNullOrEmpty(_selectedMacro.TriggerKey) ? "点击此处设置..." : _selectedMacro.TriggerKey;
                TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
                MessageBox.Show($"此按键已绑定给宏「{conflict.Name}」，请选择其他按键。", "冲突",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var keyName = KeyInterop.KeyFromVirtualKey(vkCode).ToString();
            _selectedMacro.TriggerKey = keyName;
            _selectedMacro.TriggerVirtualKeyCode = vkCode;
            _selectedMacro.TriggerType = Models.TriggerType.Keyboard;
            _selectedMacro.TriggerMouseButton = -1;
            _selectedMacro.UpdatedAt = DateTime.Now;
            _storage.Save(_selectedMacro, _macros);

            TriggerKeyBox.Text = keyName;
            _isBindingTriggerKey = false;
            TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
            RefreshMacroList();
        }

        private void BindTriggerMouseButton(int button)
        {
            if (_selectedMacro == null) return;

            // 左键不允许作为触发键（太容易误触）
            if (button == 0)
            {
                _isBindingTriggerKey = false;
                TriggerKeyBox.Text = string.IsNullOrEmpty(_selectedMacro.TriggerKey) ? "点击此处设置..." : _selectedMacro.TriggerKey;
                TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
                MessageBox.Show("鼠标左键不能作为触发键（容易误触）。\n建议使用侧键或中键。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var conflict = _macros.FirstOrDefault(m => m.Id != _selectedMacro.Id && m.TriggerType == Models.TriggerType.Mouse && m.TriggerMouseButton == button);
            if (conflict != null)
            {
                _isBindingTriggerKey = false;
                TriggerKeyBox.Text = string.IsNullOrEmpty(_selectedMacro.TriggerKey) ? "点击此处设置..." : _selectedMacro.TriggerKey;
                TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
                MessageBox.Show($"此鼠标按键已绑定给宏「{conflict.Name}」，请选择其他按键。", "冲突",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var buttonName = MouseButtonDisplayName(button);
            _selectedMacro.TriggerKey = buttonName;
            _selectedMacro.TriggerVirtualKeyCode = 0;
            _selectedMacro.TriggerType = Models.TriggerType.Mouse;
            _selectedMacro.TriggerMouseButton = button;
            _selectedMacro.UpdatedAt = DateTime.Now;
            _storage.Save(_selectedMacro, _macros);

            TriggerKeyBox.Text = buttonName;
            _isBindingTriggerKey = false;
            TriggerKeyBox.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3C));
            RefreshMacroList();
        }

        private static string MouseButtonDisplayName(int button) => button switch
        {
            0 => "🖱 左键",
            1 => "🖱 右键",
            2 => "🖱 中键",
            3 => "🖱 侧键后(X1)",
            4 => "🖱 侧键前(X2)",
            _ => $"🖱 按钮{button}"
        };

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

                var speedTag = _selectedMacro.PlaybackSpeed.ToString();
                foreach (ComboBoxItem item in SpeedCombo.Items)
                {
                    if (item.Tag?.ToString() == speedTag)
                    {
                        SpeedCombo.SelectedItem = item;
                        break;
                    }
                }

                // 更新按钮状态 — 如果该宏正在回放
                if (_player.IsMacroPlaying(_selectedMacro.Id))
                {
                    PlayButton.Visibility = Visibility.Collapsed;
                    StopButton.Visibility = Visibility.Visible;
                    RecordButton.IsEnabled = false;
                }
                else
                {
                    PlayButton.Visibility = Visibility.Visible;
                    StopButton.Visibility = Visibility.Collapsed;
                    RecordButton.IsEnabled = true;
                }

                if (!_isRefreshingMacroList)
                    _currentSequenceIndex = 0;
                UpdateEventList();
                RefreshSequenceTabs();
            }
        }

        private void NewMacro_Click(object sender, RoutedEventArgs e)
        {
            var macro = new MacroDefinition
            {
                Name = $"宏 {_macros.Count + 1}",
                Sequences = { new MacroSequence() }
            };
            _macros.Add(macro);
            _storage.SaveAll(_macros);
            RefreshMacroList();
            MacroList.SelectedItem = macro;
        }

        private void DeleteMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null) return;

            // 如果正在回放，先停止
            if (_player.IsMacroPlaying(_selectedMacro.Id))
                _player.Stop(_selectedMacro.Id);

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
            if (_selectedMacro != null && _player.IsMacroPlaying(_selectedMacro.Id))
                _player.Stop(_selectedMacro.Id);
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

        #region 事件编辑

        private void AddKeyEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null || CurrentEvents == null) return;

            var dialog = new EventEditDialog();
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && dialog.ResultEvent != null)
            {
                int insertIndex = EventList.SelectedIndex >= 0 ? EventList.SelectedIndex + 1 : CurrentEvents.Count;

                if (dialog.InsertKeyPress)
                {
                    // "按下+释放" 插入两个事件
                    var downEvt = dialog.ResultEvent;
                    var upEvt = new MacroEvent
                    {
                        Type = MacroEventType.KeyUp,
                        VirtualKeyCode = downEvt.VirtualKeyCode,
                        KeyName = downEvt.KeyName,
                        DelayMs = 30 // 默认 30ms 间隔
                    };
                    CurrentEvents.Insert(insertIndex, downEvt);
                    CurrentEvents.Insert(insertIndex + 1, upEvt);
                }
                else
                {
                    CurrentEvents.Insert(insertIndex, dialog.ResultEvent);
                }

                SaveAndRefreshEvents();
            }
        }

        private void AddDelayEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null || CurrentEvents == null) return;

            // 快捷添加延迟 — 直接弹输入框
            var dialog = new EventEditDialog();
            dialog.Owner = this;
            // 预选"延迟"类型
            dialog.Loaded += (_, _) =>
            {
                var combo = dialog.FindName("EventTypeCombo") as System.Windows.Controls.ComboBox;
                if (combo != null) combo.SelectedIndex = 3;
            };

            if (dialog.ShowDialog() == true && dialog.ResultEvent != null)
            {
                int insertIndex = EventList.SelectedIndex >= 0 ? EventList.SelectedIndex + 1 : CurrentEvents!.Count;
                CurrentEvents!.Insert(insertIndex, dialog.ResultEvent);
                SaveAndRefreshEvents();
            }
        }

        private void EditEvent_Click(object sender, RoutedEventArgs e)
        {
            EditSelectedEvent();
        }

        private void EventList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            EditSelectedEvent();
        }

        private void EditSelectedEvent()
        {
            if (_selectedMacro == null || CurrentEvents == null || EventList.SelectedIndex < 0) return;

            int index = EventList.SelectedIndex;
            if (index >= CurrentEvents.Count) return;

            var existingEvent = CurrentEvents[index];
            var dialog = new EventEditDialog(existingEvent);
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && dialog.ResultEvent != null)
            {
                CurrentEvents[index] = dialog.ResultEvent;
                SaveAndRefreshEvents();
                // 保持选中
                if (index < EventList.Items.Count)
                    EventList.SelectedIndex = index;
            }
        }

        private void DeleteEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null || CurrentEvents == null || EventList.SelectedIndex < 0) return;

            int index = EventList.SelectedIndex;
            if (index >= CurrentEvents.Count) return;

            CurrentEvents.RemoveAt(index);
            SaveAndRefreshEvents();

            // 选中相邻项
            if (CurrentEvents.Count > 0)
                EventList.SelectedIndex = Math.Min(index, CurrentEvents.Count - 1);
        }

        private void MoveEventUp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null || CurrentEvents == null || EventList.SelectedIndex <= 0) return;

            int index = EventList.SelectedIndex;
            var evt = CurrentEvents[index];
            CurrentEvents.RemoveAt(index);
            CurrentEvents.Insert(index - 1, evt);
            SaveAndRefreshEvents();
            EventList.SelectedIndex = index - 1;
        }

        private void MoveEventDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null || CurrentEvents == null || EventList.SelectedIndex < 0) return;

            int index = EventList.SelectedIndex;
            if (index >= CurrentEvents.Count - 1) return;

            var evt = CurrentEvents[index];
            CurrentEvents.RemoveAt(index);
            CurrentEvents.Insert(index + 1, evt);
            SaveAndRefreshEvents();
            EventList.SelectedIndex = index + 1;
        }

        private void SaveAndRefreshEvents()
        {
            if (_selectedMacro == null) return;
            _selectedMacro.UpdatedAt = DateTime.Now;
            _storage.Save(_selectedMacro, _macros);
            UpdateEventList();
            RefreshMacroList();
        }

        #endregion

        #region 序列管理

        private void AddSequence_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null) return;

            var newSeq = new MacroSequence { Name = $"序列 {_selectedMacro.Sequences.Count + 1}" };
            _selectedMacro.Sequences.Add(newSeq);
            _currentSequenceIndex = _selectedMacro.Sequences.Count - 1;
            SaveAndRefreshEvents();
            RefreshSequenceTabs();
        }

        private void RemoveSequence_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMacro == null || _selectedMacro.Sequences.Count <= 1) return;

            _selectedMacro.Sequences.RemoveAt(_currentSequenceIndex);
            _currentSequenceIndex = Math.Min(_currentSequenceIndex, _selectedMacro.Sequences.Count - 1);
            SaveAndRefreshEvents();
            RefreshSequenceTabs();
        }

        private void SwitchSequence(int index)
        {
            if (_selectedMacro == null || index < 0 || index >= _selectedMacro.Sequences.Count) return;

            _currentSequenceIndex = index;
            UpdateEventList();
            RefreshSequenceTabs();
        }

        private void RefreshSequenceTabs()
        {
            SequenceTabs.Items.Clear();
            if (_selectedMacro == null) return;

            for (int i = 0; i < _selectedMacro.Sequences.Count; i++)
            {
                var seq = _selectedMacro.Sequences[i];
                var btn = new System.Windows.Controls.Button
                {
                    Content = seq.Name,
                    FontSize = 11,
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = i
                };

                if (i == _currentSequenceIndex)
                {
                    btn.Style = (Style)FindResource("ModernButton");
                }
                else
                {
                    btn.Style = (Style)FindResource("GhostButton");
                }

                int capturedIndex = i;
                btn.Click += (_, _) => SwitchSequence(capturedIndex);
                SequenceTabs.Items.Add(btn);
            }
        }

        #endregion

        #region 辅助方法

        private void RefreshMacroList()
        {
            _isRefreshingMacroList = true;
            var selected = _selectedMacro;
            MacroList.ItemsSource = null;
            MacroList.ItemsSource = _macros;
            if (selected != null)
                MacroList.SelectedItem = _macros.FirstOrDefault(m => m.Id == selected.Id);
            _isRefreshingMacroList = false;
        }

        private void UpdateEventList()
        {
            EventList.Items.Clear();
            if (_selectedMacro == null || CurrentEvents == null) return;

            foreach (var evt in CurrentEvents)
            {
                EventList.Items.Add(FormatEvent(evt));
            }

            EventCountText.Text = $"事件列表（{CurrentEvents.Count} 个事件，序列 {_currentSequenceIndex + 1}/{_selectedMacro.Sequences.Count}）";
            DurationText.Text = CurrentEvents.Count > 0 ? $"总时长: {_selectedMacro.DurationInfo}" : "";
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
