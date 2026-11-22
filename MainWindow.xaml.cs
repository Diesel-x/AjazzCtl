using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AjazzCtl;

public partial class MainWindow : Window
{
    private static readonly (byte Mode, string Title)[] LightingModes =
    [
        (0, "0: Выключена"),
        (1, "1: Статичная"),
        (2, "2: Одиночное"),
        (3, "3: Инверсия"),
        (4, "4: Мерцание"),
        (5, "5: Падение"),
        (6, "6: Разноцветная"),
        (7, "7: Дыхание"),
        (8, "8: Спектр"),
        (9, "9: От центра"),
        (10, "10: Прокрутка"),
        (11, "11: Волна"),
        (12, "12: Вращение"),
        (13, "13: Взрыв"),
        (14, "14: Запуск"),
        (15, "15: Рябь"),
        (16, "16: Поток"),
        (17, "17: Пульсация"),
        (18, "18: Наклон"),
        (19, "19: Шаттл")
    ];

    private static readonly string[] ColorPresets =
    [
        "#FF8000", "#00B4FF", "#9B4DFF", "#00E676",
        "#FF2A55", "#FFD600", "#00E5FF", "#FFFFFF"
    ];

    private static readonly string[] PerKeyPresets =
    [
        "#00B4FF", "#FF2A55", "#00E676", "#FF8000", "#9B4DFF",
        "#FFD600", "#00E5FF", "#FF00FF", "#FFFFFF", "#000000"
    ];

    private readonly SemaphoreSlim _hidLock = new(1, 1);
    private readonly List<(Bitmap Frame, int DelayMs)> _frames = new();
    private readonly List<BitmapSource> _previewSources = new();
    private readonly Dictionary<byte, (byte R, byte G, byte B)> _keyColors = new();
    private readonly Dictionary<byte, (Border Box, TextBlock Label)> _keyVisuals = new();

    private readonly DispatcherTimer _animTimer = new();
    private readonly DispatcherTimer _autoTimeTimer = new();
    private readonly DispatcherTimer _lightDebounceTimer = new();
    private readonly DispatcherTimer _deviceChangeTimer = new();
    private readonly DispatcherTimer _layoutWatchTimer = new();
    private readonly KeySoundEngine _keySoundEngine = new();

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _trayIconHandle;
    private System.Windows.Forms.ToolStripItem? _trayOpenItem;
    private System.Windows.Forms.ToolStripItem? _traySyncItem;
    private System.Windows.Forms.ToolStripItem? _traySoundItem;
    private System.Windows.Forms.ToolStripItem? _trayExitItem;
    private Border? _knobVisual;
    private Border? _tftVisual;
    private System.Windows.Controls.Button? _eraserButton;
    private HwndSource? _hwndSource;

    private int _currentFrameIndex;
    private bool _isAnimPlaying;
    private bool _isUpdatingUi;
    private bool _isBusy;
    private bool _allowExit;
    private bool _disposed;
    private bool _lastUsedPerKeyMode;
    private bool _wasDeviceConnected;
    private bool _pendingAutoRestore;
    private ushort _lastDetectedLangId;
    private string _lastLangCode = "--";

    private byte _selectedLightMode = 11;
    private byte _selectedDirection;
    private MediaColor _selectedColor = MediaColor.FromRgb(255, 128, 0);
    private MediaColor _perKeyBrushColor = MediaColor.FromRgb(0, 180, 255);
    private string? _lastLoadedImagePath;

    private const int WM_DEVICECHANGE = 0x0219;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    public int DetectedDeviceCount { get; private set; }
    public bool HasPreviewFrame => _frames.Count > 0;

    public MainWindow()
    {
        InitializeComponent();
        _isUpdatingUi = true;
        try
        {
            LanguageCombo.SelectedIndex = (int)Loc.Current;
        }
        finally
        {
            _isUpdatingUi = false;
        }
        BuildLightingUi();
        BuildPerKeyKeyboardUi();
        InitializeTimers();
        InitializeTray();
        ApplyLocalization();

        SourceInitialized += MainWindow_SourceInitialized;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        RefreshDevices();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && !_disposed)
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !_disposed)
        {
            Dispatcher.BeginInvoke(() =>
            {
                AppendLog("Выход ПК из сна: проверка клавиатуры и восстановление состояния...");
                _pendingAutoRestore = true;
                _deviceChangeTimer.Stop();
                _deviceChangeTimer.Start();
            });
        }
    }

    private void BuildLightingUi()
    {
        _isUpdatingUi = true;
        try
        {
            var radioStyle = (Style)FindResource("TileRadioStyle");
            foreach (var (mode, title) in LightingModes)
            {
                var rb = new RadioButton
                {
                    Style = radioStyle,
                    Content = title,
                    Tag = mode,
                    IsChecked = mode == _selectedLightMode
                };
                rb.Checked += LightModeRadio_Checked;
                LightModesGrid.Children.Add(rb);
            }

            foreach (string hex in ColorPresets)
            {
                var col = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                var btn = new System.Windows.Controls.Button
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(col),
                    ToolTip = hex
                };
                btn.Click += (_, _) =>
                {
                    SetSelectedColor(col);
                    TriggerInstantLightIfEnabled();
                };
                ColorSwatchesPanel.Children.Add(btn);
            }
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void BuildPerKeyKeyboardUi()
    {
        foreach (string hex in PerKeyPresets)
        {
            var col = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var btn = new System.Windows.Controls.Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 5, 0),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(col),
                ToolTip = hex == "#000000" ? "Ластик (выключить LED)" : hex
            };
            btn.Click += (_, _) => SetPerKeyBrushColor(col);
            PerKeySwatchesPanel.Children.Add(btn);
            if (hex == "#000000")
                _eraserButton = btn;
        }

        // Visual Rotary Knob in top-right corner
        var knob = new Border
        {
            Width = KeyboardLayout.KnobRect.Size,
            Height = KeyboardLayout.KnobRect.Size,
            CornerRadius = new CornerRadius(KeyboardLayout.KnobRect.Size / 2.0),
            Background = new SolidColorBrush(MediaColor.FromRgb(30, 38, 54)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(78, 95, 130)),
            BorderThickness = new Thickness(2),
            ToolTip = Loc.Get("KnobToolTip")
        };
        _knobVisual = knob;
        knob.Child = new TextBlock
        {
            Text = "VOL",
            FontSize = 8,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(156, 168, 190)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Canvas.SetLeft(knob, KeyboardLayout.KnobRect.X);
        Canvas.SetTop(knob, KeyboardLayout.KnobRect.Y);
        KeyboardCanvas.Children.Add(knob);

        // Visual 0.85" TFT Screen in right area
        var tft = new Border
        {
            Width = KeyboardLayout.ScreenRect.Width,
            Height = KeyboardLayout.ScreenRect.Height,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(MediaColor.FromRgb(10, 14, 22)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(56, 165, 255)),
            BorderThickness = new Thickness(1.5),
            ToolTip = Loc.Get("TftToolTip")
        };
        _tftVisual = tft;
        tft.Child = new TextBlock
        {
            Text = "TFT",
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(90, 200, 255)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Canvas.SetLeft(tft, KeyboardLayout.ScreenRect.X);
        Canvas.SetTop(tft, KeyboardLayout.ScreenRect.Y);
        KeyboardCanvas.Children.Add(tft);

        // Interactive 81 keys
        foreach (var key in KeyboardLayout.Keys)
        {
            var label = new TextBlock
            {
                Text = key.Name,
                FontSize = key.Name.Length >= 4 ? 8.5 : 9.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            var box = new Border
            {
                Width = key.Width,
                Height = key.Height,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1.5),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{key.Name} (LED #{key.LightIndex})",
                Tag = key.LightIndex,
                Child = label
            };

            byte lightIdx = key.LightIndex;
            box.MouseLeftButtonDown += (_, e) =>
            {
                PaintKey(lightIdx, _perKeyBrushColor);
                e.Handled = true;
            };
            box.MouseRightButtonDown += (_, e) =>
            {
                PaintKey(lightIdx, MediaColor.FromRgb(0, 0, 0));
                e.Handled = true;
            };
            box.MouseEnter += (_, _) =>
            {
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                    PaintKey(lightIdx, _perKeyBrushColor);
                else if (System.Windows.Input.Mouse.RightButton == System.Windows.Input.MouseButtonState.Pressed)
                    PaintKey(lightIdx, MediaColor.FromRgb(0, 0, 0));
            };

            Canvas.SetLeft(box, key.X);
            Canvas.SetTop(box, key.Y);
            KeyboardCanvas.Children.Add(box);
            _keyVisuals[lightIdx] = (box, label);
        }

        ApplyPerKeyPreset("gamer");
    }

    private void PaintKey(byte lightIndex, MediaColor color)
    {
        _keyColors[lightIndex] = (color.R, color.G, color.B);
        UpdateKeyVisual(lightIndex, color);
    }

    private void UpdateKeyVisual(byte lightIndex, MediaColor color)
    {
        if (!_keyVisuals.TryGetValue(lightIndex, out var visual))
            return;

        bool isOff = color.R == 0 && color.G == 0 && color.B == 0;
        if (isOff)
        {
            visual.Box.Background = new SolidColorBrush(MediaColor.FromRgb(24, 30, 44));
            visual.Box.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(48, 60, 84));
            visual.Label.Foreground = new SolidColorBrush(MediaColor.FromRgb(120, 132, 155));
        }
        else
        {
            visual.Box.Background = new SolidColorBrush(color);
            byte borderR = (byte)Math.Min(255, color.R + 45);
            byte borderG = (byte)Math.Min(255, color.G + 45);
            byte borderB = (byte)Math.Min(255, color.B + 45);
            visual.Box.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(borderR, borderG, borderB));

            double lum = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
            visual.Label.Foreground = lum > 145
                ? new SolidColorBrush(MediaColor.FromRgb(16, 20, 30))
                : Brushes.White;
        }
    }

    private void ApplyPerKeyPreset(string preset)
    {
        var colors = KeyboardLayout.BuildPresetColors(preset, (_perKeyBrushColor.R, _perKeyBrushColor.G, _perKeyBrushColor.B));
        foreach (var (idx, (r, g, b)) in colors)
        {
            _keyColors[idx] = (r, g, b);
            UpdateKeyVisual(idx, MediaColor.FromRgb(r, g, b));
        }
    }

    private void SetPerKeyBrushColor(MediaColor color)
    {
        _isUpdatingUi = true;
        try
        {
            _perKeyBrushColor = color;
            PerKeyBrushPreview.Background = new SolidColorBrush(color);
            PerKeyHexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void InitializeTimers()
    {
        _animTimer.Interval = TimeSpan.FromMilliseconds(100);
        _animTimer.Tick += (_, _) => AdvanceAnimationFrame();

        _autoTimeTimer.Interval = TimeSpan.FromMinutes(30);
        _autoTimeTimer.Tick += async (_, _) => await SyncTimeAsync(silent: true);

        // Debounce timer (250 ms) to protect flash from rapid slider dragging
        _lightDebounceTimer.Interval = TimeSpan.FromMilliseconds(250);
        _lightDebounceTimer.Tick += async (_, _) =>
        {
            _lightDebounceTimer.Stop();
            if (!_isBusy && !_disposed && InstantLightApplyCheck?.IsChecked == true)
                await ApplyLightingAsync(silent: true);
        };

        // WM_DEVICECHANGE / Wake debounce timer (800 ms)
        _deviceChangeTimer.Interval = TimeSpan.FromMilliseconds(800);
        _deviceChangeTimer.Tick += async (_, _) =>
        {
            _deviceChangeTimer.Stop();
            await HandleDeviceChangeOrResumeAsync();
        };

        // Active keyboard layout watcher (250 ms, zero HID traffic unless layout actually changes)
        _layoutWatchTimer.Interval = TimeSpan.FromMilliseconds(250);
        _layoutWatchTimer.Tick += async (_, _) => await CheckKeyboardLayoutAsync();
        _layoutWatchTimer.Start();
    }

    private async Task HandleDeviceChangeOrResumeAsync()
    {
        if (_disposed)
            return;

        bool wasConnected = _wasDeviceConnected;
        RefreshDevices();

        bool shouldRestore = (_pendingAutoRestore || (!wasConnected && _wasDeviceConnected))
            && _wasDeviceConnected
            && AutoRestoreConnectCheck?.IsChecked == true;

        _pendingAutoRestore = false;

        if (shouldRestore && !_isBusy)
        {
            AppendLog("Автовосстановление: синхронизация часов и подсветки после подключения/сна...");
            await SyncTimeAsync(silent: true);
            if (_lastUsedPerKeyMode)
                await ApplyPerKeyLightingAsync(silent: true);
            else
                await ApplyLightingAsync(silent: true);
        }
    }

    private async Task CheckKeyboardLayoutAsync()
    {
        if (_disposed)
            return;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return;

        uint tid = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        ushort langId = (ushort)((long)GetKeyboardLayout(tid) & 0xFFFF);
        if (langId == 0 || langId == _lastDetectedLangId)
            return;

        _lastDetectedLangId = langId;
        bool isRu = (langId & 0xFF) == 0x19; // 0x0419 = Russian
        string langCode = isRu ? "RU" : (langId & 0xFF) == 0x09 ? "EN" : $"0x{langId:X4}";
        _lastLangCode = langCode;

        if (LayoutBadgeText is not null)
            LayoutBadgeText.Text = Loc.Format("LangBadge", langCode);

        if (LayoutIndicatorCheck?.IsChecked != true || _isBusy || !_wasDeviceConnected)
            return;

        var indicatorColor = isRu
            ? (R: (byte)255, G: (byte)59, B: (byte)0)   // RU: Orange-Red
            : (R: (byte)0, G: (byte)180, B: (byte)255); // EN: Cyan-Blue

        var preferred = GetPreferredConnectionMode();
        if (LayoutIndicatorModeCombo?.SelectedIndex == 1)
        {
            // Per-key zone indicator: CapsLock (30), Space (65), Esc (88)
            var snapshot = new Dictionary<byte, (byte R, byte G, byte B)>(_keyColors)
            {
                [30] = indicatorColor,
                [65] = indicatorColor,
                [88] = indicatorColor
            };
            byte brightness = (byte)Math.Clamp((int)PerKeyBrightnessSlider.Value, 1, 5);
            await RunHidActionAsync($"Индикатор языка ({langCode})", session =>
            {
                AjazzProtocol.SetPerKeyLighting(session, snapshot, brightness);
                return $"Индикация раскладки {langCode} (поклавишная зона Caps/Space/Esc).";
            }, preferred, silent: true);
        }
        else
        {
            byte brightness = (byte)BrightnessSlider.Value;
            await RunHidActionAsync($"Индикатор языка ({langCode})", session =>
            {
                AjazzProtocol.SetLighting(session, (byte)LightMode.Static, indicatorColor.R, indicatorColor.G, indicatorColor.B, brightness, 3, 0, colorful: false);
                return $"Индикация раскладки {langCode} (#{indicatorColor.R:X2}{indicatorColor.G:X2}{indicatorColor.B:X2}).";
            }, preferred, silent: true);
        }
    }

    private void InitializeTray()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res?.Stream is not null)
            {
                using var stream = res.Stream;
                _trayIconHandle = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            _trayIconHandle = null;
        }

        if (_trayIconHandle is null)
        {
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(37, 99, 235));
                using var brush = new SolidBrush(Color.White);
                using var font = new Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold);
                g.DrawString("A", font, brush, 1, 0);
            }
            _trayIconHandle = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        _trayOpenItem = menu.Items.Add(Loc.Get("TrayOpen"), null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        _traySyncItem = menu.Items.Add(Loc.Get("TraySyncTime"), null, (_, _) => Dispatcher.Invoke(async () => await SyncTimeAsync()));
        _traySoundItem = menu.Items.Add(Loc.Get("TrayToggleSound"), null, (_, _) => Dispatcher.Invoke(() => KeySoundEnabledCheck.IsChecked = !(KeySoundEnabledCheck.IsChecked == true)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayExitItem = menu.Items.Add(Loc.Get("TrayExit"), null, (_, _) => Dispatcher.Invoke(() =>
        {
            _allowExit = true;
            Close();
        }));

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIconHandle,
            Text = "AjazzCtl — AJAZZ AK820 Pro",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingUi || _disposed)
            return;

        Loc.Current = LanguageCombo.SelectedIndex switch
        {
            1 => AppLanguage.Ru,
            2 => AppLanguage.Zh,
            3 => AppLanguage.De,
            _ => AppLanguage.En
        };
        ApplyLocalization();
        RefreshDevices(logScan: false);
    }

    private void ApplyLocalization()
    {
        Title = Loc.Get("WindowTitle");

        // Header Bar
        LayoutBadgeText.Text = Loc.Format("LangBadge", _lastLangCode);
        ConnectionModeLabel.Text = Loc.Get("ChannelLabel");
        ConnAutoItem.Content = Loc.Get("ConnAuto");
        ConnUsbItem.Content = Loc.Get("ConnUsb");
        Conn24GItem.Content = Loc.Get("Conn24G");
        AutoSyncTimeCheck.Content = Loc.Get("AutoSyncCheck");
        AutoRestoreConnectCheck.Content = Loc.Get("AutoRestoreCheck");
        AutoRestoreConnectCheck.ToolTip = Loc.Get("AutoRestoreToolTip");
        LanguageLabel.Text = Loc.Get("LanguageLabel");
        RefreshDevicesButton.Content = Loc.Get("BtnRefresh");
        SyncTimeButton.Content = Loc.Get("BtnSyncTime");

        // Left Sidebar
        ScreenSectionTitleText.Text = Loc.Get("ScreenSectionTitle");
        ScreenDropHintText.Text = Loc.Get("ScreenDropHint");
        if (_frames.Count > 0)
            ShowFrameAt(_currentFrameIndex);
        else
            FrameInfoText.Text = Loc.Get("FrameInfoEmpty");

        SlotLabelText.Text = Loc.Get("SlotLabel");
        for (int i = 0; i < SlotCombo.Items.Count; i++)
        {
            if (SlotCombo.Items[i] is ComboBoxItem slotItem)
                slotItem.Content = Loc.Format("SlotFormat", i + 1);
        }
        if (!_isBusy)
            UploadStatusText.Text = Loc.Get("StatusReady");

        // Tab 1: Screen
        ScreenTabItem.Header = Loc.Get("TabScreen");
        ImgCardTitleText.Text = Loc.Get("ImgCardTitle");
        WirelessSlowWarningText.Text = Loc.Get("WirelessSlowWarning");
        SelectImageButton.Content = Loc.Get("BtnSelectImage");
        LoadDemoGifButton.Content = Loc.Get("BtnDemoGif");
        GifLimitLabelText.Text = Loc.Get("GifLimitLabel");
        GifLimit30Item.Content = Loc.Get("GifLimit30");
        GifLimit60Item.Content = Loc.Get("GifLimit60");
        GifLimit140Item.Content = Loc.Get("GifLimit140");
        FrameDelayLabelText.Text = Loc.Get("FrameDelayLabel");
        FrameDelayText.Text = $"{(int)FrameDelaySlider.Value} {Loc.Get("MsUnit")}";
        UploadImageButton.Content = Loc.Get("BtnUploadImage");

        // Tab 2: RGB Lighting
        LightingTabItem.Header = Loc.Get("TabLighting");
        LightModesTitleText.Text = Loc.Get("LightModesTitle");
        InstantLightApplyCheck.Content = Loc.Get("InstantApplyCheck");
        foreach (var child in LightModesGrid.Children)
        {
            if (child is RadioButton rb && rb.Tag is byte mode)
                rb.Content = Loc.GetLightingModeName(mode);
        }
        ColorPaletteTitleText.Text = Loc.Get("ColorPaletteTitle");
        PickColorButton.Content = Loc.Get("BtnPickColor");
        ColorfulCheck.Content = Loc.Get("ColorfulCheck");
        BrightnessSpeedDirTitleText.Text = Loc.Get("BrightnessSpeedDirTitle");
        BrightnessLabelText.Text = Loc.Get("BrightnessLabel");
        SpeedLabelText.Text = Loc.Get("SpeedLabel");
        DirectionLabelText.Text = Loc.Get("DirectionLabel");
        DirLeftRadio.Content = Loc.Get("DirLeft");
        DirRightRadio.Content = Loc.Get("DirRight");
        DirUpRadio.Content = Loc.Get("DirUp");
        DirDownRadio.Content = Loc.Get("DirDown");
        ApplyLightButton.Content = Loc.Get("BtnApplyLight");

        // Tab 3: Per-Key RGB
        PerKeyTabItem.Header = Loc.Get("TabPerKey");
        PerKeyMapTitleText.Text = Loc.Get("PerKeyMapTitle");
        PerKeyMapHintText.Text = Loc.Get("PerKeyMapHint");
        PerKeyZonesTitleText.Text = Loc.Get("PerKeyZonesTitle");
        ZoneAllBtn.Content = Loc.Get("ZoneAll");
        ZoneWasdBtn.Content = Loc.Get("ZoneWasd");
        ZoneArrowsBtn.Content = Loc.Get("ZoneArrows");
        ZoneFnBtn.Content = Loc.Get("ZoneFn");
        ZoneNumbersBtn.Content = Loc.Get("ZoneNumbers");
        ZoneLettersBtn.Content = Loc.Get("ZoneLetters");
        ZoneModifiersBtn.Content = Loc.Get("ZoneModifiers");
        ZoneOffBtn.Content = Loc.Get("ZoneOff");
        ThemeCyberpunkBtn.Content = Loc.Get("ThemeCyberpunk");
        ThemeGamerBtn.Content = Loc.Get("ThemeGamer");
        ThemeGradientBtn.Content = Loc.Get("ThemeGradient");
        ThemeRainbowBtn.Content = Loc.Get("ThemeRainbow");
        PerKeyBrushLabelText.Text = Loc.Get("PerKeyBrushLabel");
        PerKeyPickColorButton.Content = Loc.Get("BtnPickColor");
        PerKeyBrightnessLabelText.Text = Loc.Get("PerKeyBrightnessLabel");
        ApplyPerKeyButton.Content = Loc.Get("BtnApplyPerKey");
        if (_knobVisual is not null) _knobVisual.ToolTip = Loc.Get("KnobToolTip");
        if (_tftVisual is not null) _tftVisual.ToolTip = Loc.Get("TftToolTip");
        if (_eraserButton is not null) _eraserButton.ToolTip = Loc.Get("EraserToolTip");

        // Tab 4: Settings & Uninstall
        SettingsTabItem.Header = Loc.Get("TabSettings");
        DebounceTitleText.Text = Loc.Get("DebounceTitle");
        Debounce1Radio.Content = Loc.Get("Debounce1");
        Debounce2Radio.Content = Loc.Get("Debounce2");
        Debounce3Radio.Content = Loc.Get("Debounce3");
        Debounce4Radio.Content = Loc.Get("Debounce4");
        Debounce5Radio.Content = Loc.Get("Debounce5");
        SleepTitleText.Text = Loc.Get("SleepTitle");
        Sleep0Radio.Content = Loc.Get("Sleep0");
        Sleep1Radio.Content = Loc.Get("Sleep1");
        Sleep2Radio.Content = Loc.Get("Sleep2");
        Sleep3Radio.Content = Loc.Get("Sleep3");
        GameModeTitleText.Text = Loc.Get("GameModeTitle");
        DisableWinCheck.Content = Loc.Get("DisableWinCheck");
        DisableAltF4Check.Content = Loc.Get("DisableAltF4Check");
        DisableAltTabCheck.Content = Loc.Get("DisableAltTabCheck");
        FnSwitchCheck.Content = Loc.Get("FnSwitchCheck");
        ApplySettingsButton.Content = Loc.Get("BtnApplySettings");
        LayoutIndTitleText.Text = Loc.Get("LayoutIndTitle");
        LayoutIndicatorCheck.Content = Loc.Get("LayoutIndCheck");
        LayoutIndModeLabelText.Text = Loc.Get("LayoutIndModeLabel");
        LayoutIndModeAllItem.Content = Loc.Get("LayoutIndModeAll");
        LayoutIndModeZoneItem.Content = Loc.Get("LayoutIndModeZone");
        UninstallTitleText.Text = Loc.Get("UninstallTitle");
        UninstallDescText.Text = Loc.Get("UninstallDesc");
        UninstallButton.Content = Loc.Get("BtnUninstall");

        // Tab 5: Key Sounds
        SoundsTabItem.Header = Loc.Get("TabSounds");
        SoundCardTitleText.Text = Loc.Get("SoundCardTitle");
        KeySoundEnabledCheck.Content = Loc.Get("SoundEnableCheck");
        SoundVolumeLabelText.Text = Loc.Get("SoundVolumeLabel");
        KeySoundSpecialKeysCheck.Content = Loc.Get("SoundSpecialKeysCheck");
        KeySoundPitchVarCheck.Content = Loc.Get("SoundPitchVarCheck");
        SoundTestLabelText.Text = Loc.Get("SoundTestLabel");
        SoundTestNormalBtn.Content = Loc.Get("BtnTestNormal");
        SoundTestSpaceBtn.Content = Loc.Get("BtnTestSpace");
        SoundTestEnterBtn.Content = Loc.Get("BtnTestEnter");
        SoundProfilesTitleText.Text = Loc.Get("SoundProfilesTitle");
        ProfThockyRadio.Content = Loc.Get("ProfThocky");
        ProfClickyBlueRadio.Content = Loc.Get("ProfClickyBlue");
        ProfSilentTactileRadio.Content = Loc.Get("ProfSilentTactile");
        ProfTypewriterRadio.Content = Loc.Get("ProfTypewriter");
        ProfBubblePopRadio.Content = Loc.Get("ProfBubblePop");
        ProfSciFiRadio.Content = Loc.Get("ProfSciFi");
        LoadCustomWavButton.Content = Loc.Get("BtnLoadWav");
        CustomWavProfileRadio.Content = Loc.Get("ProfCustomWav");
        if (!_keySoundEngine.HasCustomWav)
            CustomWavStatusText.Text = Loc.Get("CustomWavNone");

        // Log & Tray
        LogTitleText.Text = Loc.Get("LogTitle");
        MinimizeToTrayCheck.Content = Loc.Get("MinimizeTrayCheck");
        ClearLogButton.Content = Loc.Get("BtnClearLog");
        DeveloperLabelRun.Text = Loc.Get("DeveloperLabel");

        if (_trayOpenItem is not null) _trayOpenItem.Text = Loc.Get("TrayOpen");
        if (_traySyncItem is not null) _traySyncItem.Text = Loc.Get("TraySyncTime");
        if (_traySoundItem is not null) _traySoundItem.Text = Loc.Get("TrayToggleSound");
        if (_trayExitItem is not null) _trayExitItem.Text = Loc.Get("TrayExit");
    }

    public void ShutdownTray()
    {
        if (_disposed)
            return;
        _disposed = true;

        _animTimer.Stop();
        _autoTimeTimer.Stop();
        _lightDebounceTimer.Stop();
        _deviceChangeTimer.Stop();
        _layoutWatchTimer.Stop();
        _keySoundEngine.Dispose();

        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        ClearFrames();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _trayIconHandle?.Dispose();
        _trayIconHandle = null;
        _hidLock.Dispose();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowExit && MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            _notifyIcon?.ShowBalloonTip(1500, Loc.Get("TrayMinimizedTitle"), Loc.Get("TrayMinimizedBody"), System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        ShutdownTray();
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private ConnectionMode? GetPreferredConnectionMode()
    {
        return ConnectionModeCombo.SelectedIndex switch
        {
            1 => ConnectionMode.UsbWired,
            2 => ConnectionMode.Wireless24G,
            _ => null
        };
    }

    private byte GetSelectedSlot() => (byte)(Math.Clamp(SlotCombo.SelectedIndex, 0, 9) + 1);

    private void RefreshDevices(bool logScan = true)
    {
        try
        {
            var devices = HidDiscovery.EnumerateAjazzDevices();
            DetectedDeviceCount = devices.Count;

            bool hasUsb = devices.Any(d => d.IsUsbControlFeature);
            bool hasUsbBulk = devices.Any(d => d.IsUsbScreenBulk);
            bool has24G = devices.Any(d => d.IsWirelessVendor);
            _wasDeviceConnected = hasUsb || has24G;

            if (hasUsb && has24G)
            {
                StatusText.Text = Loc.Format("StatusUsbAnd24G", hasUsbBulk ? " + Bulk" : "");
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(74, 222, 128));
            }
            else if (hasUsb)
            {
                StatusText.Text = Loc.Format("StatusUsbOnly", hasUsbBulk ? ", Bulk 4097B" : "");
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(74, 222, 128));
            }
            else if (has24G)
            {
                StatusText.Text = Loc.Get("Status24GOnly");
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(74, 222, 128));
            }
            else if (devices.Count > 0)
            {
                StatusText.Text = Loc.Format("StatusNoControl", devices.Count);
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(251, 191, 36));
            }
            else
            {
                StatusText.Text = Loc.Get("StatusNotFound");
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
            }

            if (logScan)
                AppendLog($"HID scan: found {devices.Count} collections (USB={hasUsb}, USB-Bulk={hasUsbBulk}, 2.4G={has24G}).");
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Get("StatusScanError");
            StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(248, 113, 113));
            AppendLog($"{Loc.Get("StatusScanError")}: {ex.Message}");
        }
    }

    private int GetGifMaxFramesLimit()
    {
        if (OptimizeGifCombo?.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int limit))
        {
            return limit;
        }
        return 30;
    }

    private async Task LoadImageFileAsync(string path)
    {
        int maxFrames = GetGifMaxFramesLimit();
        _lastLoadedImagePath = path;
        UploadStatusText.Text = Loc.Get("StatusDecoding");

        var (optimized, rawCount) = await Task.Run(() =>
        {
            var raw = ScreenWidgetRenderer.LoadImageFrames(path);
            try
            {
                var opt = ScreenWidgetRenderer.OptimizeFrames(raw, maxFrames);
                return (opt, raw.Count);
            }
            finally
            {
                foreach (var (bmp, _) in raw)
                    bmp.Dispose();
            }
        });

        if (_disposed)
        {
            foreach (var (bmp, _) in optimized)
                bmp.Dispose();
            return;
        }

        SetPreviewFrames(optimized);
        if (optimized.Count > 0)
        {
            _isUpdatingUi = true;
            try
            {
                int delay = Math.Clamp(optimized[0].DelayMs, 30, 500);
                FrameDelaySlider.Value = Math.Round(delay / 10.0) * 10;
                FrameDelayText.Text = $"{(int)FrameDelaySlider.Value} {Loc.Get("MsUnit")}";
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }
        UploadStatusText.Text = Loc.Get("StatusReady");
        AppendLog($"{Path.GetFileName(path)}: {optimized.Count}/{rawCount}, 128×128 RGB565.");
    }

    private void SetPreviewFrames(List<(Bitmap Frame, int DelayMs)> newFrames)
    {
        _animTimer.Stop();
        ClearFrames();

        _frames.AddRange(newFrames);
        foreach (var (bmp, _) in _frames)
            _previewSources.Add(BitmapToImageSource(bmp));

        _currentFrameIndex = 0;

        _isUpdatingUi = true;
        try
        {
            bool multi = _frames.Count > 1;
            FrameScrubberSlider.IsEnabled = multi;
            PlayPauseButton.IsEnabled = multi;
            FrameScrubberSlider.Minimum = 1;
            FrameScrubberSlider.Maximum = Math.Max(1, _frames.Count);
            FrameScrubberSlider.Value = 1;
        }
        finally
        {
            _isUpdatingUi = false;
        }

        if (_frames.Count > 0)
        {
            ShowFrameAt(0);
            if (_frames.Count > 1)
            {
                _isAnimPlaying = true;
                _animTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_frames[0].DelayMs, 30, 500));
                _animTimer.Start();
            }
            else
            {
                _isAnimPlaying = false;
            }
        }
        else
        {
            PreviewImage.Source = null;
            FrameInfoText.Text = Loc.Get("FrameCountZero");
            _isAnimPlaying = false;
        }
    }

    private void ShowFrameAt(int index)
    {
        if (index < 0 || index >= _previewSources.Count)
            return;

        _currentFrameIndex = index;
        PreviewImage.Source = _previewSources[index];
        int delay = _frames[index].DelayMs;
        int totalBytes = AjazzProtocol.ScreenHeaderLength + _frames.Count * AjazzProtocol.ScreenFrameBytes;
        string sizeLabel = totalBytes >= 1024 * 1024
            ? $"{totalBytes / (1024.0 * 1024.0):F2} MB"
            : $"{totalBytes / 1024.0:F0} KB";
        FrameInfoText.Text = Loc.Format("FrameInfoFormat", index + 1, _frames.Count, sizeLabel, delay);

        _isUpdatingUi = true;
        try
        {
            FrameScrubberSlider.Value = index + 1;
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void AdvanceAnimationFrame()
    {
        if (_frames.Count <= 1)
            return;

        int next = (_currentFrameIndex + 1) % _frames.Count;
        ShowFrameAt(next);
        _animTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_frames[next].DelayMs, 30, 500));
    }

    private void ClearFrames()
    {
        PreviewImage.Source = null;
        _previewSources.Clear();
        foreach (var (bmp, _) in _frames)
            bmp.Dispose();
        _frames.Clear();
    }

    private static BitmapSource BitmapToImageSource(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int size = stride * bmp.Height;
            byte[] pixels = new byte[size];
            Marshal.Copy(data.Scan0, pixels, 0, size);

            var wb = new WriteableBitmap(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, bmp.Width, bmp.Height), pixels, stride, 0);
            wb.Freeze();
            return wb;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private async Task SyncTimeAsync(bool silent = false)
    {
        DateTime now = DateTime.Now;
        byte slot = GetSelectedSlot();
        var preferred = GetPreferredConnectionMode();

        await RunHidActionAsync(Loc.Get("ActionSyncTime"), session =>
        {
            AjazzProtocol.SyncTime(session, now, slot);
            return $"{Loc.Get("ActionSyncTime")} ({session.Mode}): {now:yyyy-MM-dd HH:mm:ss}";
        }, preferred, silent);
    }

    private async Task UploadCurrentFramesAsync(string label, bool silent = false)
    {
        if (_frames.Count == 0)
        {
            if (!silent)
                AppendLog(Loc.Get("MsgNoFrames"));
            return;
        }

        byte[] screenBuffer = AjazzProtocol.BuildScreenBuffer(_frames);
        int frameCount = _frames.Count;
        byte slot = GetSelectedSlot();
        var preferred = GetPreferredConnectionMode();

        UploadProgressBar.Value = 0;
        UploadStatusText.Text = Loc.Format("StatusSending", frameCount);

        await RunHidActionAsync(Loc.Get("ActionUploadImg"), session =>
        {
            AjazzProtocol.UploadScreen(session, screenBuffer, slot, pct =>
            {
                if (!_disposed)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        UploadProgressBar.Value = Math.Clamp(pct, 0, 100);
                        UploadStatusText.Text = Loc.Format("StatusSendingPct", pct);
                    });
                }
            });
            return $"{Loc.Get("ActionUploadImg")} ({session.Mode}): {frameCount} fr., {screenBuffer.Length} B.";
        }, preferred, silent);

        UploadStatusText.Text = Loc.Get("StatusDone");
    }

    private async Task ApplyLightingAsync(bool silent = false)
    {
        _lastUsedPerKeyMode = false;
        byte mode = _selectedLightMode;
        byte r = _selectedColor.R;
        byte g = _selectedColor.G;
        byte b = _selectedColor.B;
        byte brightness = (byte)BrightnessSlider.Value;
        byte speed = (byte)SpeedSlider.Value;
        byte direction = _selectedDirection;
        bool colorful = ColorfulCheck.IsChecked == true;
        var preferred = GetPreferredConnectionMode();

        await RunHidActionAsync(Loc.Get("ActionApplyLight"), session =>
        {
            AjazzProtocol.SetLighting(session, mode, r, g, b, brightness, speed, direction, colorful);
            return $"{Loc.Get("ActionApplyLight")} ({session.Mode}): mode={mode}, #{r:X2}{g:X2}{b:X2}, br={brightness}, sp={speed}.";
        }, preferred, silent);
    }

    private async Task ApplyPerKeyLightingAsync(bool silent = false)
    {
        _lastUsedPerKeyMode = true;
        var snapshot = new Dictionary<byte, (byte R, byte G, byte B)>(_keyColors);
        byte brightness = (byte)Math.Clamp((int)PerKeyBrightnessSlider.Value, 1, 5);
        var preferred = GetPreferredConnectionMode();

        await RunHidActionAsync(Loc.Get("ActionApplyPerKey"), session =>
        {
            AjazzProtocol.SetPerKeyLighting(session, snapshot, brightness);
            return $"{Loc.Get("ActionApplyPerKey")} ({session.Mode}): {snapshot.Count} keys, br={brightness}.";
        }, preferred, silent);
    }

    private async Task ApplySettingsAsync()
    {
        bool disableWin = DisableWinCheck.IsChecked == true;
        bool disableAltF4 = DisableAltF4Check.IsChecked == true;
        bool disableAltTab = DisableAltTabCheck.IsChecked == true;
        bool fnSwitch = FnSwitchCheck.IsChecked == true;
        byte debounce = GetSelectedTagByte(DebounceGrid, 1);
        var sleep = (SleepTimeout)Math.Clamp(GetSelectedTagByte(SleepGrid, 2), (byte)0, (byte)3);
        var preferred = GetPreferredConnectionMode();

        await RunHidActionAsync(Loc.Get("ActionApplySettings"), session =>
        {
            AjazzProtocol.SetSystemSettings(session, disableWin, disableAltF4, disableAltTab, debounce, sleep, fnSwitch);
            return $"{Loc.Get("ActionApplySettings")} ({session.Mode}): debounce={debounce}, sleep={sleep}.";
        }, preferred);
    }

    private static byte GetSelectedTagByte(Panel container, byte fallback)
    {
        foreach (var child in container.Children)
        {
            if (child is RadioButton rb && rb.IsChecked == true && byte.TryParse(rb.Tag?.ToString(), out byte val))
                return val;
        }
        return fallback;
    }

    private async Task RunHidActionAsync(
        string actionName,
        Func<AjazzHidSession, string> operation,
        ConnectionMode? preferredMode,
        bool silent = false)
    {
        if (!await _hidLock.WaitAsync(0))
        {
            if (!silent)
                AppendLog($"{actionName}: {Loc.Get("ActionDeviceBusy")}");
            return;
        }

        _isBusy = true;
        SetActionsEnabled(false);
        try
        {
            string resultMessage = await Task.Run(() =>
            {
                using var session = HidDiscovery.OpenSession(preferredMode);
                return operation(session);
            });
            AppendLog(resultMessage);
        }
        catch (Exception ex)
        {
            AppendLog($"{actionName} — {Loc.Get("MsgErrTitle")}: {ex.Message}");
            if (!silent)
                System.Windows.MessageBox.Show(this, ex.Message, actionName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isBusy = false;
            if (!_disposed)
            {
                SetActionsEnabled(true);
                _hidLock.Release();
            }
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        RefreshDevicesButton.IsEnabled = enabled;
        SyncTimeButton.IsEnabled = enabled;
        UploadImageButton.IsEnabled = enabled;
        ApplyLightButton.IsEnabled = enabled;
        ApplyPerKeyButton.IsEnabled = enabled;
        ApplySettingsButton.IsEnabled = enabled;
        UninstallButton.IsEnabled = enabled;
    }

    private void AppendLog(string message)
    {
        if (_disposed)
            return;
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLogDirect(line));
            return;
        }
        AppendLogDirect(line);
    }

    private void AppendLogDirect(string line)
    {
        LogTextBox.AppendText(line);
        LogTextBox.ScrollToEnd();
    }

    private void SetSelectedColor(MediaColor color)
    {
        _isUpdatingUi = true;
        try
        {
            _selectedColor = color;
            ColorSwatchPreview.Background = new SolidColorBrush(color);
            HexColorBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedValueText.Text = color.R.ToString();
            GreenValueText.Text = color.G.ToString();
            BlueValueText.Text = color.B.ToString();
        }
        finally
        {
            _isUpdatingUi = false;
        }
    }

    private void TriggerInstantLightIfEnabled()
    {
        if (!_isUpdatingUi && InstantLightApplyCheck?.IsChecked == true)
        {
            _lightDebounceTimer.Stop();
            _lightDebounceTimer.Start();
        }
    }

    // Event handlers
    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private async void SyncTimeButton_Click(object sender, RoutedEventArgs e) => await SyncTimeAsync();

    private void AutoSyncTimeCheck_Changed(object sender, RoutedEventArgs e)
    {
        _autoTimeTimer.IsEnabled = AutoSyncTimeCheck.IsChecked == true;
        AppendLog(_autoTimeTimer.IsEnabled
            ? "Автосинхронизация времени включена (интервал 30 мин)."
            : "Автосинхронизация времени отключена.");
    }

    private void ScreenPreview_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void ScreenPreview_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            return;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            try
            {
                await LoadImageFileAsync(files[0]);
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка загрузки файла: {ex.Message}");
            }
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_frames.Count <= 1)
            return;

        _isAnimPlaying = !_isAnimPlaying;
        _animTimer.IsEnabled = _isAnimPlaying;
    }

    private void FrameScrubberSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || _frames.Count <= 1)
            return;

        int idx = Math.Clamp((int)FrameScrubberSlider.Value - 1, 0, _frames.Count - 1);
        ShowFrameAt(idx);
    }

    private void KeySoundEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _disposed)
            return;

        bool enable = KeySoundEnabledCheck.IsChecked == true;
        try
        {
            if (enable)
            {
                _keySoundEngine.Start();
                AppendLog($"Звуки нажатия клавиш включены (профиль: {_keySoundEngine.Profile}, громкость: {(int)(_keySoundEngine.Volume * 100)}%).");
            }
            else
            {
                _keySoundEngine.Stop();
                AppendLog("Звуки нажатия клавиш отключены.");
            }
        }
        catch (Exception ex)
        {
            KeySoundEnabledCheck.IsChecked = false;
            AppendLog($"Ошибка запуска звукового движка: {ex.Message}");
            System.Windows.MessageBox.Show(this, ex.Message, "Звуки клавиш", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void KeySoundVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_keySoundEngine is null || KeySoundVolumeText is null)
            return;

        int pct = (int)Math.Round(KeySoundVolumeSlider.Value);
        _keySoundEngine.Volume = pct / 100f;
        KeySoundVolumeText.Text = $"{pct}%";
    }

    private void KeySoundOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_keySoundEngine is null || KeySoundSpecialKeysCheck is null || KeySoundPitchVarCheck is null)
            return;

        _keySoundEngine.UseSpecialKeys = KeySoundSpecialKeysCheck.IsChecked == true;
        _keySoundEngine.PitchVariation = KeySoundPitchVarCheck.IsChecked == true;
    }

    private void KeySoundProfileRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_keySoundEngine is null)
            return;

        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse<KeySoundProfile>(tag, out var profile))
        {
            _keySoundEngine.Profile = profile;
            if (IsLoaded)
                AppendLog($"Выбран звуковой профиль клавиш: {profile}.");
        }
    }

    private void KeySoundTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag)
            return;

        if (Enum.TryParse<KeySoundKind>(tag, out var kind))
        {
            try
            {
                _keySoundEngine.PlayTest(kind);
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка воспроизведения пробы: {ex.Message}");
            }
        }
    }

    private void LoadCustomWavButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите WAV-файл звука клавиши",
            Filter = "WAV аудио (*.wav)|*.wav|Все файлы (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            _keySoundEngine.LoadCustomWav(dlg.FileName);
            _keySoundEngine.Profile = KeySoundProfile.CustomWav;
            CustomWavProfileRadio.IsEnabled = true;
            CustomWavProfileRadio.IsChecked = true;
            CustomWavStatusText.Text = Path.GetFileName(dlg.FileName);
            AppendLog($"Загружен свой WAV-звук: {Path.GetFileName(dlg.FileName)}");
            _keySoundEngine.PlayTest(KeySoundKind.Normal);
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка загрузки WAV: {ex.Message}");
            System.Windows.MessageBox.Show(this, ex.Message, "Загрузка WAV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SelectImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите изображение или GIF-анимацию",
            Filter = "Изображения и GIF (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Все файлы (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            await LoadImageFileAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            AppendLog($"{Loc.Get("MsgErrOpenImg")}: {ex.Message}");
            System.Windows.MessageBox.Show(this, ex.Message, Loc.Get("MsgErrOpenImg"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadDemoGifButton_Click(object sender, RoutedEventArgs e)
    {
        string[] candidates =
        [
            @"c:\Repos\AjazzSoft\gif\AJAZZ AK820Pro\0.gif",
            @"C:\Program Files (x86)\AJAZZ AK820Pro\gif\AJAZZ AK820Pro\0.gif"
        ];
        string? found = candidates.FirstOrDefault(File.Exists);
        if (found is null)
        {
            AppendLog(Loc.Get("MsgDemoNotFound"));
            return;
        }
        try
        {
            await LoadImageFileAsync(found);
        }
        catch (Exception ex)
        {
            AppendLog($"{Loc.Get("MsgErrOpenImg")}: {ex.Message}");
        }
    }

    private async void OptimizeGifCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || string.IsNullOrEmpty(_lastLoadedImagePath) || !File.Exists(_lastLoadedImagePath))
            return;
        try
        {
            await LoadImageFileAsync(_lastLoadedImagePath);
        }
        catch (Exception ex)
        {
            AppendLog($"{Loc.Get("MsgErrOpenImg")}: {ex.Message}");
        }
    }

    private void FrameDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FrameDelayText is null)
            return;
        int delay = (int)FrameDelaySlider.Value;
        FrameDelayText.Text = $"{delay} {Loc.Get("MsUnit")}";

        if (_isUpdatingUi || _frames.Count == 0)
            return;

        for (int i = 0; i < _frames.Count; i++)
            _frames[i] = (_frames[i].Frame, delay);

        _animTimer.Interval = TimeSpan.FromMilliseconds(delay);
        ShowFrameAt(_currentFrameIndex);
    }

    private async void UploadImageButton_Click(object sender, RoutedEventArgs e) =>
        await UploadCurrentFramesAsync("картинки/GIF");

    private void LightModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && byte.TryParse(rb.Tag?.ToString(), out byte mode))
        {
            _selectedLightMode = mode;
            TriggerInstantLightIfEnabled();
        }
    }

    private void DirectionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && byte.TryParse(rb.Tag?.ToString(), out byte dir))
        {
            _selectedDirection = dir;
            TriggerInstantLightIfEnabled();
        }
    }

    private void PickColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = Color.FromArgb(_selectedColor.R, _selectedColor.G, _selectedColor.B),
            FullOpen = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SetSelectedColor(MediaColor.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
            TriggerInstantLightIfEnabled();
        }
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi ||
            RedSlider is null || GreenSlider is null || BlueSlider is null ||
            RedValueText is null || GreenValueText is null || BlueValueText is null)
        {
            return;
        }

        var col = MediaColor.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        SetSelectedColor(col);
        TriggerInstantLightIfEnabled();
    }

    private void HexColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi ||
            HexColorBox is null || ColorSwatchPreview is null ||
            RedSlider is null || GreenSlider is null || BlueSlider is null ||
            RedValueText is null || GreenValueText is null || BlueValueText is null)
        {
            return;
        }

        string raw = HexColorBox.Text.Trim().TrimStart('#');
        if (raw.Length == 6 &&
            byte.TryParse(raw[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(raw[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(raw[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            _isUpdatingUi = true;
            try
            {
                _selectedColor = MediaColor.FromRgb(r, g, b);
                ColorSwatchPreview.Background = new SolidColorBrush(_selectedColor);
                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
                RedValueText.Text = r.ToString();
                GreenValueText.Text = g.ToString();
                BlueValueText.Text = b.ToString();
            }
            finally
            {
                _isUpdatingUi = false;
            }
            TriggerInstantLightIfEnabled();
        }
    }

    private void LightParam_Changed(object sender, RoutedEventArgs e)
    {
        if (BrightnessValueText is not null && BrightnessSlider is not null)
            BrightnessValueText.Text = ((int)BrightnessSlider.Value).ToString();
        if (SpeedValueText is not null && SpeedSlider is not null)
            SpeedValueText.Text = ((int)SpeedSlider.Value).ToString();

        TriggerInstantLightIfEnabled();
    }

    private async void ApplyLightButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyLightingAsync();

    private void PerKeyZoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string zone)
            return;

        MediaColor targetColor = zone == "off" ? MediaColor.FromRgb(0, 0, 0) : _perKeyBrushColor;
        foreach (var k in KeyboardLayout.Keys)
        {
            bool match = zone switch
            {
                "all" or "off" => true,
                "wasd" => k.IsWasd,
                "arrows" => k.Category == KeyCategory.Arrow,
                "fn" => k.Category == KeyCategory.Function,
                "numbers" => k.Category == KeyCategory.Number,
                "letters" => k.Category == KeyCategory.Letter,
                "modifiers" => k.Category == KeyCategory.Modifier,
                _ => false
            };
            if (match)
                PaintKey(k.LightIndex, targetColor);
        }
        AppendLog($"Поклавишная раскраска: применена зона '{zone}'.");
    }

    private void PerKeyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string preset)
            return;
        ApplyPerKeyPreset(preset);
        AppendLog($"Применена тема раскраски клавиш: '{preset}'.");
    }

    private void PerKeyPickColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = Color.FromArgb(_perKeyBrushColor.R, _perKeyBrushColor.G, _perKeyBrushColor.B),
            FullOpen = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SetPerKeyBrushColor(MediaColor.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    private void PerKeyHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingUi || PerKeyHexBox is null || PerKeyBrushPreview is null)
            return;

        string raw = PerKeyHexBox.Text.Trim().TrimStart('#');
        if (raw.Length == 6 &&
            byte.TryParse(raw[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(raw[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(raw[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            _perKeyBrushColor = MediaColor.FromRgb(r, g, b);
            PerKeyBrushPreview.Background = new SolidColorBrush(_perKeyBrushColor);
        }
    }

    private void PerKeyBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PerKeyBrightnessText is not null && PerKeyBrightnessSlider is not null)
            PerKeyBrightnessText.Text = ((int)PerKeyBrightnessSlider.Value).ToString();
    }

    private async void ApplyPerKeyButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyPerKeyLightingAsync();

    private void LayoutIndicatorCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = LayoutIndicatorCheck.IsChecked == true;
        AppendLog(enabled
            ? "Индикация раскладки клавиатуры (RU / EN) включена."
            : "Индикация раскладки клавиатуры (RU / EN) отключена.");
        if (enabled)
        {
            _lastDetectedLangId = 0; // force immediate check
            _ = CheckKeyboardLayoutAsync();
        }
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e) =>
        await ApplySettingsAsync();

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            Loc.Get("UninstallConfirmBody"),
            Loc.Get("UninstallConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            _keySoundEngine.Stop();
            _allowExit = true;
            ShutdownTray();
            UninstallHelper.PerformFullUninstall();
            System.Windows.Application.Current?.Shutdown();
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Loc.Get("UninstallErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeveloperLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppendLog($"{Loc.Get("MsgErrTitle")}: {ex.Message}");
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) =>
        LogTextBox.Clear();
}
