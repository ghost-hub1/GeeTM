using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using GeeTM.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using System.Threading.Tasks;

namespace GeeTM.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private double _offsetX;
    private double _offsetY;
    private const double OffsetStep = 3; 
    public event Action? SettingsSaved;
    private string _originalSkin;
    private string _originalLook;
    private bool _isLoading = true;

    private static readonly (string Name, string Hex)[] BgPresets =
    {
        ("Charcoal (default)", "#0E1116"),
        ("Deep Violet", "#14101F"),
        ("Midnight Blue", "#0B111C"),
        ("Espresso", "#1A1410"),
        ("Pure Black (OLED)", "#000000"),
        ("Slate Gray", "#1C1F26"),
        ("Graphite", "#202226"),
    };

    // 10 candidates, not 8 - Cascadia Code and JetBrains Mono aren't bundled
    // with Windows by default (they need Windows Terminal or a JetBrains
    // product installed), so on a system without either, the installed-font
    // filter below would correctly drop them, leaving only 6. Verdana and
    // Tahoma have shipped with every Windows install for decades, so they
    // reliably backfill the list back up to 8 regardless of what's actually
    // on any given machine.
    private static readonly string[] CuratedFonts =
    {
        "Cascadia Mono", "Consolas", "Courier New", "Lucida Console",
        "Cascadia Code", "Segoe UI", "Segoe UI Mono", "JetBrains Mono",
        "Verdana", "Tahoma",
    };
    private static List<string>? _fontChoices;

    private static List<string> GetFontChoices(string currentFamily)
    {
        if (_fontChoices == null)
        {
            try
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Fonts.SystemFontFamilies) installed.Add(f.Source);
                _fontChoices = CuratedFonts.Where(installed.Contains).ToList();
                if (_fontChoices.Count == 0) _fontChoices.Add("Segoe UI");
            }
            catch
            {
                _fontChoices = CuratedFonts.ToList();
            }
        }
        var list = new List<string>(_fontChoices);
        if (!string.IsNullOrWhiteSpace(currentFamily) &&
            !list.Contains(currentFamily, StringComparer.OrdinalIgnoreCase))
        {
            list.Insert(0, currentFamily);
        }
        return list;
    }

    private static readonly string[] TodayLabelChoices = { "Today: ", "Total: ", "Used: ", "Month: ", "" };

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Current;
        _originalSkin = _settings.Skin; 
        _originalLook = _settings.UiLook;
        LoadGeneral();
        LoadAppearance();
        LoadLayout();
        LoadDataAndUnits();
        LoadAbout();
        _isLoading = false;
        
        SourceInitialized += (s, e) =>
        {
            ApplyRoundedCorners();
            ApplyCorrectCacheScale();
        };
    }

    /// <summary>
    /// CacheMode="BitmapCache" on each tab's scrollable content (set in XAML)
    /// rasterizes that mostly-static, visually rich content once and slides
    /// the resulting bitmap during scrolling, instead of re-rendering every
    /// border, rounded corner and gradient from vector primitives on every
    /// single scroll frame - this is the actual fix for the reported general
    /// scrolling lag, which is a rendering-weight issue, not a scroll-logic
    /// bug (SmoothScroll.cs itself is unrelated and untouched).
    ///
    /// The one thing that needs care: WPF's default BitmapCache renders at
    /// scale 1.0 regardless of the screen's actual DPI. Left at that default,
    /// the cached bitmap would be rasterized at a LOWER effective resolution
    /// than a high-DPI screen actually displays, then upscaled - visibly
    /// blurry text and borders on any high-DPI display, which would be a
    /// real, visible regression. Setting RenderAtScale to the window's actual
    /// runtime DPI (whatever it is - 100%, 150%, 200%, 225%, anything) keeps
    /// the cached content exactly as sharp as it was uncached, correctly, on
    /// every display this ships to - not just the one it was tested on.
    /// </summary>
    private void ApplyCorrectCacheScale()
    {
        try
        {
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            foreach (var panel in new[] { ScrollCachePanel1, ScrollCachePanel2, ScrollCachePanel3, ScrollCachePanel4, ScrollCachePanel5 })
            {
                if (panel.CacheMode is BitmapCache cache)
                {
                    cache.RenderAtScale = dpiScale;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"SettingsWindow.ApplyCorrectCacheScale failed: {ex.Message}");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private void ApplyRoundedCorners()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void LoadGeneral()
    {
        foreach (var skin in SkinManager.AvailableSkins) SkinCombo.Items.Add(skin);
        SkinCombo.SelectedItem = SkinManager.AvailableSkins.Contains(_settings.Skin) ? _settings.Skin : "Aurora";
        LookPremium.IsChecked = _settings.UiLook == "Premium";
        LookClassic.IsChecked = _settings.UiLook != "Premium";
        EmbedCheck.IsChecked = _settings.EmbedInTaskbar;
        bool elevated = ElevationHelper.IsRunningElevated();
        EmbedCheck.IsEnabled = true;
        EmbedHint.Text = "Current state: " + TaskbarHostService.LastStatus;
        StartupCheck.IsChecked = _settings.StartWithWindows;
        FullscreenHideCheck.IsChecked = _settings.HideWhenFullscreen;
        FullscreenOverlayCheck.IsChecked = _settings.FullscreenOverlayEnabled;
        ClickThroughCheck.IsChecked = _settings.WidgetClickThrough;
        ProcessBreakdownCheck.IsChecked = _settings.ShowPerProcessBreakdown;
        VpnNotificationsCheck.IsChecked = _settings.VpnNotificationsEnabled;
        ThreatCheckEnabledCheck.IsChecked = _settings.ThreatCheckEnabled;
        AbuseIpDbKeyBox.Text = _settings.AbuseIpDbApiKey;
        AdminHint.Text = elevated
            ? "Running as administrator - per-process tracking is available, and taskbar embedding works too."
            : "Not running as administrator. Turning this on will offer to restart GeeTM elevated.";
        Wire(PollSlider, PollValue, _settings.PollIntervalMs, v => $"{v:0} ms");
        Wire(ScrollSpeedSlider, ScrollSpeedValue, _settings.ScrollSpeed, v => $"{v:0.00}x");
    }

    private void LoadAppearance()
    {
        switch (_settings.ColorMode)
        {
            case WidgetColorMode.AutoDarker: ColorAuto.IsChecked = true; break;
            case WidgetColorMode.AutoExact: ColorAutoExact.IsChecked = true; break;
            default: ColorCustom.IsChecked = true; break;
        }
        Wire(DarkenSlider, DarkenValue, _settings.AutoDarkenAmount, v => $"{v * 100:0}%");
        foreach (var preset in BgPresets) BgPresetCombo.Items.Add(preset.Name);
        var matched = Array.Find(BgPresets, p => string.Equals(p.Hex, _settings.WidgetBackgroundHex, StringComparison.OrdinalIgnoreCase));
        BgPresetCombo.SelectedItem = matched.Name ?? BgPresets[0].Name;
        BgHexBox.Text = _settings.WidgetBackgroundHex;
        UpdateColorPreview();
        PillBorderEnabledCheck.IsChecked = _settings.PillBorderEnabled;
        ShapeOnePod.IsChecked = _settings.PillShapeStyle == "OnePod";
        ShapeTwoPods.IsChecked = _settings.PillShapeStyle != "OnePod";
        PillBorderHexBox.Text = _settings.PillBorderColorHex;
        PillBorderHexBox_TextChanged(this, null!);
        Wire(PillBorderThicknessSlider, PillBorderThicknessValue, _settings.PillBorderThickness, v => $"{v:0.#} px");
        Wire(OpacitySlider, OpacityValue, _settings.WidgetOpacity, v => $"{v * 100:0}%");
        Wire(CornerRadiusSlider, CornerRadiusValue, _settings.WidgetCornerRadius, v => $"{v:0} px");
        ShadowCheck.IsChecked = _settings.WidgetShadow;
        foreach (var f in GetFontChoices(_settings.WidgetFontFamily)) FontCombo.Items.Add(f);
        FontCombo.SelectedItem = FontCombo.Items.Contains(_settings.WidgetFontFamily)
            ? _settings.WidgetFontFamily
            : FontCombo.Items[0];
        Wire(FontSizeSlider, FontSizeValue, _settings.WidgetFontSize, v => $"{v:0.#} pt");
    }

    private void LoadLayout()
    {
        ShowUpCheck.IsChecked = _settings.ShowUploadRow;
        ShowDownCheck.IsChecked = _settings.ShowDownloadRow;
        ShowTodayCheck.IsChecked = _settings.ShowTodayInWidget;
        TodayMonthCheck.IsChecked = _settings.TodayShowsMonth;
        TotalBeforeCheck.IsChecked = _settings.TotalBeforeSpeed;
        Wire(WidthSlider, WidthValue, _settings.WidgetWidth, v => $"{v:0}");
        Wire(HeightSlider, HeightValue, _settings.WidgetHeight, v => $"{v:0}");
        Wire(PaddingHSlider, PaddingHValue, _settings.WidgetPaddingH, v => $"{v:0}");
        Wire(PaddingVSlider, PaddingVValue, _settings.WidgetPaddingV, v => $"{v:0}");
        Wire(IconGapSlider, IconGapValue, _settings.WidgetIconTextGap, v => $"{v:0}");
        Wire(RowGapSlider, RowGapValue, _settings.WidgetRowGap, v => $"{v:0}");
        Wire(DigitUnitGapSlider, DigitUnitGapValue, _settings.WidgetDigitUnitGap, v => $"{v:0}");
        DigitsBoldCheck.IsChecked = _settings.WidgetDigitsBold;
        UnitBoldCheck.IsChecked = _settings.WidgetUnitBold;
        foreach (var label in TodayLabelChoices) TodayLabelCombo.Items.Add(label == "" ? "(no label)" : label);
        TodayLabelCombo.SelectedItem = TodayLabelChoices.Contains(_settings.TodayLabelText)
            ? (_settings.TodayLabelText == "" ? "(no label)" : _settings.TodayLabelText)
            : "Today: ";
        Wire(TodayFontSizeSlider, TodayFontSizeValue, _settings.TodayFontSize, v => $"{v:0.#}");
        Wire(TodayPaddingHSlider, TodayPaddingHValue, _settings.TodayPaddingH, v => $"{v:0}");
        Wire(TodayPaddingVSlider, TodayPaddingVValue, _settings.TodayPaddingV, v => $"{v:0}");
        Wire(GroupGapSlider, GroupGapValue, _settings.WidgetGroupGap, v => $"{v:0}");
        Wire(TodayDigitUnitGapSlider, TodayDigitUnitGapValue, _settings.TodayDigitUnitGap, v => $"{v:0}");
        TodayDigitsBoldCheck.IsChecked = _settings.TodayDigitsBold;
        TodayUnitBoldCheck.IsChecked = _settings.TodayUnitBold;
        RotatingPillEnabledCheck.IsChecked = _settings.RotatingPillEnabled;
        RotateShowIpCheck.IsChecked = _settings.RotatePillShowIp;
        RotateShowFlagCheck.IsChecked = _settings.RotatePillShowFlag;
        IpTargetPillCombo.SelectedIndex = _settings.IpTargetPill == "Speed" ? 1 : 0;
        FlagTargetPillCombo.SelectedIndex = _settings.FlagTargetPill == "Speed" ? 1 : 0;
        Wire(RotatingIntervalSlider, RotatingIntervalValue, _settings.RotatingPillIntervalSeconds, v => $"{v:0}s");
        EmbeddedFadeCheck.IsChecked = _settings.EmbeddedFadeTransitionEnabled;
        ManualPositionCheck.IsChecked = _settings.WidgetManualPosition;
        PosXBox.Text = _settings.WidgetX.ToString("0");
        PosYBox.Text = _settings.WidgetY.ToString("0");
        _offsetX = _settings.WidgetOffsetX;
        _offsetY = _settings.WidgetOffsetY;
        OffsetXLabel.Text = _offsetX.ToString("0");
        OffsetYLabel.Text = _offsetY.ToString("0");
    }

    private void LoadDataAndUnits()
    {
        BinaryUnitsCheck.IsChecked = _settings.UseBinaryUnits;
        BitsCheck.IsChecked = _settings.ShowSpeedInBits;
        if (_settings.SpeedDecimalPlaces == 1) Decimals1.IsChecked = true; else Decimals2.IsChecked = true;
        DailyLimitBox.Text = _settings.DailyLimitBytes == 0 ? "0" : UnitFormatter.BytesToGigabytes(_settings.DailyLimitBytes).ToString("0.##");
        MonthlyLimitBox.Text = _settings.MonthlyLimitBytes == 0 ? "0" : UnitFormatter.BytesToGigabytes(_settings.MonthlyLimitBytes).ToString("0.##");
        UpdateLimitUnitLabels();
        BinaryUnitsCheck.Checked += (_, _) => UpdateLimitUnitLabels();
        BinaryUnitsCheck.Unchecked += (_, _) => UpdateLimitUnitLabels();
        Wire(ChartWindowSlider, ChartWindowValue, _settings.ChartWindowSeconds, v => $"{v:0} s");
    }

    private void LoadAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version != null ? $"Version {version.Major}.{version.Minor}.{version.Build}" : "Version 1.0";
        DataFolderText.Text = SettingsService.DataFolder;
    }

    private void UpdateLimitUnitLabels()
    {
        string unit = (BinaryUnitsCheck.IsChecked ?? true) ? "GiB" : "GB";
        DailyLimitUnit.Text = unit;
        MonthlyLimitUnit.Text = unit;
    }

    private static void Wire(Slider slider, TextBlock readout, double initial, Func<double, string> format)
    {
        slider.Value = Math.Clamp(initial, slider.Minimum, slider.Maximum);
        readout.Text = format(slider.Value);
        slider.ValueChanged += (_, _) => readout.Text = format(slider.Value);
    }

    private void UpdateColorPreview()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(BgHexBox.Text)!;
            ColorPreview.Background = new SolidColorBrush(color);
        }
        catch { }
    }

    private void BgHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateColorPreview();
        if (!_isLoading && IsValidHexColor(BgHexBox.Text)) ColorCustom.IsChecked = true;
    }

    private void PillBorderHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(PillBorderHexBox.Text)!;
            PillBorderColorPreview.Background = new SolidColorBrush(color);
        }
        catch { }
    }

    private void OffsetXMinus_Click(object sender, RoutedEventArgs e) { _offsetX -= OffsetStep; OffsetXLabel.Text = _offsetX.ToString("0"); }
    private void OffsetXPlus_Click(object sender, RoutedEventArgs e) { _offsetX += OffsetStep; OffsetXLabel.Text = _offsetX.ToString("0"); }
    private void OffsetYMinus_Click(object sender, RoutedEventArgs e) { _offsetY -= OffsetStep; OffsetYLabel.Text = _offsetY.ToString("0"); }
    private void OffsetYPlus_Click(object sender, RoutedEventArgs e) { _offsetY += OffsetStep; OffsetYLabel.Text = _offsetY.ToString("0"); }
    private void OffsetReset_Click(object sender, RoutedEventArgs e)
    {
        _offsetX = 0; _offsetY = 0;
        OffsetXLabel.Text = "0";
        OffsetYLabel.Text = "0";
    }

    private void BgPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BgPresetCombo.SelectedItem is string name)
        {
            var match = Array.Find(BgPresets, p => p.Name == name);
            if (match.Hex != null)
            {
                BgHexBox.Text = match.Hex;
                if (!_isLoading) ColorCustom.IsChecked = true; 
            }
        }
    }

    private void SkinCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkinCombo.SelectedItem is string skin) SkinManager.Apply(skin); 
    }

    private void LookOption_Checked(object sender, RoutedEventArgs e)
    {
        LookManager.Apply(LookPremium.IsChecked == true ? "Premium" : "Classic");
    }

    private void ResetCompact_Click(object sender, RoutedEventArgs e)
    {
        WidthSlider.Value = 100;
        HeightSlider.Value = 28;
        PaddingHSlider.Value = 5;
        PaddingVSlider.Value = 2;
        IconGapSlider.Value = 2;
        RowGapSlider.Value = 0;
        DigitUnitGapSlider.Value = 2;
        TodayFontSizeSlider.Value = 10;
        TodayPaddingHSlider.Value = 6;
        TodayPaddingVSlider.Value = 2;
        GroupGapSlider.Value = 5;
        TodayDigitUnitGapSlider.Value = 2;
        CornerRadiusSlider.Value = 5;
        SetStatus("Compact layout applied - press Save to keep it.");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenPath(SettingsService.DataFolder);
    private void OpenLog_Click(object sender, RoutedEventArgs e)
        => OpenPath(System.IO.Path.Combine(SettingsService.DataFolder, "geetm.log"));

    private static void OpenPath(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(SettingsService.DataFolder);
                path = SettingsService.DataFolder;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not open {path}: {ex.Message}");
        }
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Reset every GeeTM setting to its default? Your usage totals are kept.",
            "GeeTM - Reset settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var fresh = SettingsService.ResetToDefaults();
        SkinManager.Apply(fresh.Skin);
        SettingsSaved?.Invoke();
        Close();
    }

    private void ApplySettingsFromUi()
    {
        _settings.Skin = SkinCombo.SelectedItem as string ?? "Aurora";
        _settings.UiLook = LookPremium.IsChecked == true ? "Premium" : "Classic";
        _settings.EmbedInTaskbar = EmbedCheck.IsChecked ?? false;
        _settings.StartWithWindows = StartupCheck.IsChecked ?? false;
        _settings.HideWhenFullscreen = FullscreenHideCheck.IsChecked ?? true;
        _settings.FullscreenOverlayEnabled = FullscreenOverlayCheck.IsChecked ?? false;
        _settings.WidgetClickThrough = ClickThroughCheck.IsChecked ?? false;
        _settings.ShowPerProcessBreakdown = ProcessBreakdownCheck.IsChecked ?? true;
        _settings.VpnNotificationsEnabled = VpnNotificationsCheck.IsChecked ?? false;
        _settings.ThreatCheckEnabled = ThreatCheckEnabledCheck.IsChecked ?? false;
        _settings.AbuseIpDbApiKey = AbuseIpDbKeyBox.Text.Trim();
        _settings.PollIntervalMs = (int)PollSlider.Value;
        _settings.ScrollSpeed = ScrollSpeedSlider.Value;
        _settings.ColorMode = ColorAuto.IsChecked == true ? WidgetColorMode.AutoDarker
            : ColorAutoExact.IsChecked == true ? WidgetColorMode.AutoExact
            : WidgetColorMode.Custom;
        _settings.AutoDarkenAmount = DarkenSlider.Value;
        _settings.WidgetBackgroundHex = IsValidHexColor(BgHexBox.Text) ? BgHexBox.Text : _settings.WidgetBackgroundHex;
        _settings.WidgetOpacity = OpacitySlider.Value;
        _settings.WidgetCornerRadius = CornerRadiusSlider.Value;
        _settings.WidgetShadow = ShadowCheck.IsChecked ?? true;
        _settings.PillBorderEnabled = PillBorderEnabledCheck.IsChecked ?? false;
        _settings.PillShapeStyle = ShapeOnePod.IsChecked == true ? "OnePod" : "TwoPods";
        if (IsValidHexColor(PillBorderHexBox.Text)) _settings.PillBorderColorHex = PillBorderHexBox.Text.Trim();
        _settings.PillBorderThickness = PillBorderThicknessSlider.Value;
        _settings.WidgetFontFamily = FontCombo.SelectedItem as string ?? _settings.WidgetFontFamily;
        _settings.WidgetFontSize = FontSizeSlider.Value;
        _settings.ShowUploadRow = ShowUpCheck.IsChecked ?? true;
        _settings.ShowDownloadRow = ShowDownCheck.IsChecked ?? true;
        _settings.ShowTodayInWidget = ShowTodayCheck.IsChecked ?? true;
        _settings.TodayShowsMonth = TodayMonthCheck.IsChecked ?? false;
        _settings.TotalBeforeSpeed = TotalBeforeCheck.IsChecked ?? false;
        _settings.WidgetWidth = WidthSlider.Value;
        _settings.WidgetHeight = HeightSlider.Value;
        _settings.WidgetPaddingH = PaddingHSlider.Value;
        _settings.WidgetPaddingV = PaddingVSlider.Value;
        _settings.WidgetIconTextGap = IconGapSlider.Value;
        _settings.WidgetRowGap = RowGapSlider.Value;
        _settings.WidgetDigitUnitGap = DigitUnitGapSlider.Value;
        _settings.WidgetDigitsBold = DigitsBoldCheck.IsChecked ?? true;
        _settings.WidgetUnitBold = UnitBoldCheck.IsChecked ?? false;
        var labelChoice = TodayLabelCombo.SelectedItem as string ?? "Today: ";
        _settings.TodayLabelText = labelChoice == "(no label)" ? "" : labelChoice;
        _settings.TodayFontSize = TodayFontSizeSlider.Value;
        _settings.TodayPaddingH = TodayPaddingHSlider.Value;
        _settings.TodayPaddingV = TodayPaddingVSlider.Value;
        _settings.WidgetGroupGap = GroupGapSlider.Value;
        _settings.TodayDigitUnitGap = TodayDigitUnitGapSlider.Value;
        _settings.TodayDigitsBold = TodayDigitsBoldCheck.IsChecked ?? true;
        _settings.TodayUnitBold = TodayUnitBoldCheck.IsChecked ?? false;
        _settings.RotatingPillEnabled = RotatingPillEnabledCheck.IsChecked ?? false;
        _settings.RotatePillShowIp = RotateShowIpCheck.IsChecked ?? false;
        _settings.RotatePillShowFlag = RotateShowFlagCheck.IsChecked ?? false;
        _settings.IpTargetPill = IpTargetPillCombo.SelectedIndex == 1 ? "Speed" : "Today";
        _settings.FlagTargetPill = FlagTargetPillCombo.SelectedIndex == 1 ? "Speed" : "Today";
        _settings.RotatingPillIntervalSeconds = (int)RotatingIntervalSlider.Value;
        _settings.EmbeddedFadeTransitionEnabled = EmbeddedFadeCheck.IsChecked ?? false;
        _settings.WidgetManualPosition = ManualPositionCheck.IsChecked ?? false;
        _settings.WidgetOffsetX = _offsetX;
        _settings.WidgetOffsetY = _offsetY;
        if (double.TryParse(PosXBox.Text, out var x)) _settings.WidgetX = x;
        if (double.TryParse(PosYBox.Text, out var y)) _settings.WidgetY = y;
        _settings.UseBinaryUnits = BinaryUnitsCheck.IsChecked ?? true;
        _settings.ShowSpeedInBits = BitsCheck.IsChecked ?? false;
        _settings.SpeedDecimalPlaces = Decimals1.IsChecked == true ? 1 : 2;
        _settings.DailyLimitBytes = double.TryParse(DailyLimitBox.Text, out var dGb) && dGb > 0
            ? UnitFormatter.GigabytesToBytes(dGb) : 0;
        _settings.MonthlyLimitBytes = double.TryParse(MonthlyLimitBox.Text, out var mGb) && mGb > 0
            ? UnitFormatter.GigabytesToBytes(mGb) : 0;
        _settings.ChartWindowSeconds = (int)ChartWindowSlider.Value;
        SettingsService.Save(_settings);
        _originalSkin = _settings.Skin;
        _originalLook = _settings.UiLook;
        StartupManager.SetEnabled(_settings.StartWithWindows);
        SettingsSaved?.Invoke();
        EmbedHint.Text = "Current state: " + TaskbarHostService.LastStatus;
        SetStatus($"Saved at {DateTime.Now:HH:mm:ss}");
        OfferRelaunchIfNeeded();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void OfferRelaunchIfNeeded()
    {
        bool elevated = ElevationHelper.IsRunningElevated();
        if (_settings.ShowPerProcessBreakdown && !elevated)
        {
            var result = MessageBox.Show(
                "Per-process tracking needs administrator rights. Restart GeeTM elevated now?",
                "GeeTM - Restart as administrator?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes) AppRelauncher.RelaunchElevated();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySettingsFromUi();

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        SaveSuccessBanner.Visibility = Visibility.Visible;
        await Task.Delay(3000);
        SaveSuccessBanner.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SkinManager.Apply(_originalSkin);
        LookManager.Apply(_originalLook);
        Close();
    }

    private static bool IsValidHexColor(string text)
    {
        try { ColorConverter.ConvertFromString(text); return true; }
        catch { return false; }
    }
}


