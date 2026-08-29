using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using GeeTM.Models;
using GeeTM.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace GeeTM.Views;

public partial class Dashboard : Window
{
    private readonly ObservableCollection<double> _downSeries = new();
    private readonly ObservableCollection<double> _upSeries = new();

    public Action? OnSettingsRequested { get; set; }
    public Action? OnAdapterRequested { get; set; }
    public Action? OnExitRequested { get; set; }
    public UsageHistoryService? HistoryService { get; set; }
    public DataCapService? DataCapService { get; set; }

    private int MaxPoints
    {
        get
        {
            var s = SettingsService.Current;
            int poll = Math.Max(100, s.PollIntervalMs);
            return Math.Clamp(s.ChartWindowSeconds * 1000 / poll, 10, 2000);
        }
    }

    public Dashboard()
    {
        InitializeComponent();
        BuildChart();
        StateChanged += (_, _) => UpdateMaximizeState();
        if (!ElevationHelper.IsRunningElevated() || !SettingsService.Current.ShowPerProcessBreakdown)
        {
            ProcessEmptyHint.Visibility = Visibility.Visible;
        }
        
        SourceInitialized += (s, e) => ApplyRoundedCorners();
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

    private void Settings_Click(object sender, RoutedEventArgs e) => OnSettingsRequested?.Invoke();
    private void Adapter_Click(object sender, RoutedEventArgs e) => OnAdapterRequested?.Invoke();
    private void Exit_Click(object sender, RoutedEventArgs e) => OnExitRequested?.Invoke();

    // In-place view switching between Dashboard/History/Data Caps, mirroring
    // the Settings window's tab pattern. The active view's nav button is
    // disabled (which is also what drives its highlighted styling, via the
    // NavButton style's IsEnabled trigger) while the other two stay
    // clickable to switch away.
    private void NavDashboard_Click(object sender, RoutedEventArgs e) => ShowDashboardView("Dashboard");
    private void NavHistory_Click(object sender, RoutedEventArgs e) => ShowDashboardView("History");
    private void NavDataCaps_Click(object sender, RoutedEventArgs e) => ShowDashboardView("DataCaps");

    private void ShowDashboardView(string view)
    {
        try
        {
            DashboardMainView.Visibility = view == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            HistoryView.Visibility = view == "History" ? Visibility.Visible : Visibility.Collapsed;
            DataCapsView.Visibility = view == "DataCaps" ? Visibility.Visible : Visibility.Collapsed;

            NavDashboard.IsEnabled = view != "Dashboard";
            NavHistory.IsEnabled = view != "History";
            NavDataCaps.IsEnabled = view != "DataCaps";

            if (view == "History") RefreshHistoryView();
            else if (view == "DataCaps") RefreshDataCapsView();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Dashboard.ShowDashboardView failed: {ex.Message}");
        }
    }

    private void RefreshHistoryView()
    {
        try
        {
            if (HistoryService == null) { HistoryEmptyHint.Visibility = Visibility.Visible; return; }

            var rows = new List<object>();
            foreach (var day in HistoryService.GetRecentHistory(30))
            {
                // Busiest adapter first within each day - the one someone is
                // most likely checking on.
                var adapters = new List<Models.AdapterDayUsage>(day.Adapters);
                adapters.Sort((a, b) => (b.BytesReceived + b.BytesSent).CompareTo(a.BytesReceived + a.BytesSent));

                foreach (var a in adapters)
                {
                    rows.Add(new
                    {
                        DateDisplay = day.Day.ToString("MMM d"),
                        a.AdapterName,
                        DownDisplay = UnitFormatter.TotalString(a.BytesReceived),
                        UpDisplay = UnitFormatter.TotalString(a.BytesSent),
                        LocalTagVisibility = a.IsLocal ? Visibility.Visible : Visibility.Collapsed,
                    });
                }
            }

            HistoryList.ItemsSource = rows;
            HistoryEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Dashboard.RefreshHistoryView failed: {ex.Message}");
        }
    }

    private void RefreshDataCapsView()
    {
        try
        {
            CapErrorHint.Visibility = Visibility.Collapsed;
            if (DataCapService == null) { CapsEmptyHint.Visibility = Visibility.Visible; return; }

            var dangerBrush = (Brush)FindResource("DangerBrush");
            var accentBrush = (Brush)FindResource("AccentBrush");

            var rows = new List<object>();
            foreach (var cap in DataCapService.Caps)
            {
                long used = DataCapService.GetTodayUsage(cap.ProcessName);
                double pct = cap.DailyCapBytes > 0 ? Math.Min(100.0, 100.0 * used / cap.DailyCapBytes) : 0;
                rows.Add(new
                {
                    cap.ProcessName,
                    UsageDisplay = $"{UnitFormatter.TotalString(used)} / {UnitFormatter.TotalString(cap.DailyCapBytes)}",
                    ProgressPercent = pct,
                    ProgressColor = pct >= 100 ? dangerBrush : accentBrush,
                });
            }

            CapsList.ItemsSource = rows;
            CapsEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Dashboard.RefreshDataCapsView failed: {ex.Message}");
        }
    }

    private void AddCap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CapErrorHint.Visibility = Visibility.Collapsed;

            string name = CapProcessNameBox.Text.Trim();
            // Accept "chrome.exe" or "chrome" equally, but store without the
            // extension - ProcessNetUsage.ProcessName never includes one, so
            // keeping it here would mean the cap silently never matches.
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];

            if (string.IsNullOrWhiteSpace(name))
            {
                CapErrorHint.Text = "Enter a process name first.";
                CapErrorHint.Visibility = Visibility.Visible;
                return;
            }

            if (!double.TryParse(CapAmountBox.Text.Trim(), out double amount) || amount <= 0)
            {
                CapErrorHint.Text = "Enter a positive number for the limit.";
                CapErrorHint.Visibility = Visibility.Visible;
                return;
            }

            bool isGb = CapUnitCombo.SelectedIndex == 1;
            long bytes = (long)(amount * (isGb ? 1024L * 1024 * 1024 : 1024L * 1024));

            DataCapService?.SetCap(name, bytes);
            CapProcessNameBox.Text = "";
            CapAmountBox.Text = "";
            RefreshDataCapsView();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Dashboard.AddCap_Click failed: {ex.Message}");
            CapErrorHint.Text = "Something went wrong adding that cap - check the log for details.";
            CapErrorHint.Visibility = Visibility.Visible;
        }
    }

    private void RemoveCap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: string processName })
            {
                DataCapService?.SetCap(processName, 0); // 0 = remove, per DataCapService.SetCap's contract
                RefreshDataCapsView();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Dashboard.RemoveCap_Click failed: {ex.Message}");
        }
    }

    // Sidebar collapse: expanded (labels + brand text visible) by default,
    // collapses to an icons-only rail. The nav buttons keep their icon column
    // and simply lose the label width, so nothing needs re-templating - the
    // rail just narrows and the text elements hide.
    private bool _sidebarCollapsed;
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        var vis = _sidebarCollapsed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        SidebarColumn.Width = new System.Windows.GridLength(_sidebarCollapsed ? 62 : 204);
        BrandText.Visibility = vis;

        foreach (var btn in new[] { NavDashboard, NavSettings, NavAdapter, NavExit })
        {
            Services.NavState.SetLabelVisibility(btn, vis);
            // In collapsed mode the label is hidden, so surface it as a tooltip
            // instead; the button's own Content is the label text.
            if (_sidebarCollapsed) btn.ToolTip = btn.Content;
            else btn.ClearValue(ToolTipProperty);
        }

        CollapseBtn.ToolTip = _sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar";
    }

    private SKColor ThemeColor(string key, SKColor fallback)
    {
        try
        {
            if (TryFindResource(key) is Color c) return new SKColor(c.R, c.G, c.B);
            if (Application.Current?.TryFindResource(key) is Color appColor)
                return new SKColor(appColor.R, appColor.G, appColor.B);
        }
        catch { }
        return fallback;
    }

    private void BuildChart()
    {
        var down = ThemeColor("DownColor", new SKColor(0x38, 0xBD, 0xF8));
        var up = ThemeColor("UpColor", new SKColor(0xFB, 0x92, 0x3C));
        var muted = ThemeColor("TextMutedColor", new SKColor(0x8B, 0x93, 0xA1));
        SpeedChart.Series = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = _downSeries, Name = "Download",
                Stroke = new SolidColorPaint(down, 2.2f),
                Fill = new SolidColorPaint(down.WithAlpha(38)),
                GeometrySize = 0, LineSmoothness = 0.6
            },
            new LineSeries<double>
            {
                Values = _upSeries, Name = "Upload",
                Stroke = new SolidColorPaint(up, 2.2f),
                Fill = new SolidColorPaint(up.WithAlpha(28)),
                GeometrySize = 0, LineSmoothness = 0.6
            }
        };
        SpeedChart.XAxes = new[] { new Axis { LabelsPaint = null, SeparatorsPaint = null } };
        SpeedChart.YAxes = new[]
        {
            new Axis
            {
                Labeler = v => $"{v:0.#} {UnitFormatter.ChartUnitLabel()}",
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(muted),
                SeparatorsPaint = new SolidColorPaint(muted.WithAlpha(28)) { StrokeThickness = 1 },
                MinLimit = 0
            }
        };
    }

    public void RefreshTheme()
    {
        _downSeries.Clear();
        _upSeries.Clear();
        BuildChart();
    }

    public void PushSample(NetSample sample, UsageTotals totals)
    {
        var (downVal, downUnit) = UnitFormatter.Speed(sample.DownloadBytesPerSec);
        var (upVal, upUnit) = UnitFormatter.Speed(sample.UploadBytesPerSec);
        DownSpeedBig.Text = downVal; DownUnitBig.Text = downUnit;
        UpSpeedBig.Text = upVal; UpUnitBig.Text = upUnit;
        AdapterName.Text = sample.AdapterName;
        _downSeries.Add(UnitFormatter.ToChartUnit(sample.DownloadBytesPerSec));
        _upSeries.Add(UnitFormatter.ToChartUnit(sample.UploadBytesPerSec));
        int cap = MaxPoints;
        while (_downSeries.Count > cap) _downSeries.RemoveAt(0);
        while (_upSeries.Count > cap) _upSeries.RemoveAt(0);
        long today = totals.BytesReceivedToday + totals.BytesSentToday;
        long month = totals.BytesReceivedMonth + totals.BytesSentMonth;
        TodayTotal.Text = UnitFormatter.TotalString(today);
        MonthTotal.Text = UnitFormatter.TotalString(month);
        var s = SettingsService.Current;
        if (s.DailyLimitBytes > 0)
        {
            double pct = today * 100.0 / s.DailyLimitBytes;
            TodayLimitHint.Text = $"{pct:0}% OF {UnitFormatter.TotalString(s.DailyLimitBytes)} CAP";
            TodayLimitHint.Foreground = (Brush)FindResource(pct >= 100 ? "DangerBrush" : "TextMutedBrush");
        }
        else
        {
            TodayLimitHint.Text = "NO DAILY CAP SET";
            TodayLimitHint.Foreground = (Brush)FindResource("TextMutedBrush");
        }
    }

    public void PushProcessList(IReadOnlyList<ProcessNetUsage> list)
    {
        ProcessList.ItemsSource = list.Select(p => new
        {
            p.ProcessName,
            DownDisplay = UnitFormatter.SpeedString(p.DownloadBytesPerSec),
            UpDisplay = UnitFormatter.SpeedString(p.UploadBytesPerSec)
        }).ToList();
        ProcessEmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeState()
    {
        bool max = WindowState == WindowState.Maximized;
        RootShell.Padding = max ? new Thickness(7) : new Thickness(0);
        RootShell.BorderThickness = max ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = max ? "\u2750" : "\u25A1";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}


