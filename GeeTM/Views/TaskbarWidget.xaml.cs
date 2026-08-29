using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using GeeTM.Services;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MouseEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace GeeTM.Views;

public partial class TaskbarWidget : Window
{
    private readonly NetworkMonitorService _monitor = new();
    private readonly UsageTrackerService _usage = new();
    private readonly UsageHistoryService _history = new();
    private readonly DataCapService _dataCaps = new();
    private readonly ProcessNetworkService _processMonitor = new();
    private readonly FullscreenDetectionService _fullscreenDetector = new();
    private readonly VpnDetectionService _vpnDetector = new();
    private OverlayWindow? _overlay;
    private readonly NativeEmbeddedWidget _nativeWidget = new();
    private readonly System.Windows.Threading.DispatcherTimer _nativeRepositionTimer;
    private readonly WidgetSurface _surface = new();
    private OverlayGuardService? _guard;
    private bool _embedded;
    private bool _suppressTopmost;
    private NotifyIcon? _trayIcon;
    private Dashboard? _dashboard;
    private readonly System.Windows.Threading.DispatcherTimer _redockTimer;
    private readonly System.Windows.Threading.DispatcherTimer _suppressionFailsafeTimer;
    private readonly System.Windows.Threading.DispatcherTimer _colorRefreshTimer;
    private uint _taskbarCreatedMsg;

    public TaskbarWidget()
    {
        InitializeComponent();
        _surface.SurfaceResized += OnSurfaceResized;
        _surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;
        _surface.MouseRightButtonUp += Surface_MouseRightButtonUp;
        
        Loaded += (_, _) =>
        {
            SetupTrayIcon();
            _monitor.PreferredAdapterName = SettingsService.Current.PreferredAdapter;
            _monitor.PollIntervalMs = SettingsService.Current.PollIntervalMs;
            StartMonitoring();
            if (SettingsService.Current.ShowPerProcessBreakdown) _processMonitor.Start();
            EnterMode(SettingsService.Current.EmbedInTaskbar);
            foreach (var delayMs in new[] { 1500, 3000, 6000 })
            {
                _ = Task.Delay(delayMs).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(RepositionForMode)));
            }
        };
        
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(new Action(RepositionForMode));
        _redockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _redockTimer.Tick += (_, _) => { if (!_suppressTopmost && !_embedded) DockOrPosition(); };
        _nativeRepositionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nativeRepositionTimer.Tick += (_, _) => { if (_embedded) { _nativeWidget.Reposition(); _nativeWidget.EnsureTopAmongSiblings(); } };
        _suppressionFailsafeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _suppressionFailsafeTimer.Tick += (_, _) =>
        {
            if (_suppressTopmost && !_fullscreenDetector.IsFullscreenNow())
            {
                AppLog.Write("Suppression failsafe: fullscreen no longer present - restoring widget.");
                RestoreFromSuppression();
            }
        };
        _colorRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _colorRefreshTimer.Tick += (_, _) =>
        {
            if (SettingsService.Current.ColorMode != WidgetColorMode.Custom) _surface.ApplyColors();
            if (_embedded) _nativeWidget.RefreshColors();
            _surface.ApplyRotatingPillContent(); // ApplyColors() resets the Today label text; re-apply rotation so it doesn't flicker back to normal content for a frame
        };
        _colorRefreshTimer.Start();
        
        _fullscreenDetector.FullscreenStarted += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_embedded) return; 
            if (!SettingsService.Current.HideWhenFullscreen) return;
            _suppressTopmost = true;
            _guard?.Suspend();
            AppLog.Write("Widget: suppressing due to fullscreen detection.");
            Visibility = Visibility.Collapsed;

            if (SettingsService.Current.FullscreenOverlayEnabled)
            {
                try
                {
                    _overlay ??= new OverlayWindow();
                    _overlay.PositionBottomRight();
                    _overlay.Show();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Overlay show failed: {ex.Message}");
                }
            }
        }));
        _fullscreenDetector.FullscreenEnded += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreFromSuppression();
            try { _overlay?.Hide(); }
            catch (Exception ex) { AppLog.Write($"Overlay hide failed: {ex.Message}"); }
        }));
        
        SourceInitialized += (_, _) =>
        {
            var handle = GetHandle();
            ApplySpecialWindowStyles();
            SetClickThrough(SettingsService.Current.WidgetClickThrough);
            _fullscreenDetector.SetOwnHandle(handle);
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            _fullscreenDetector.Start();
            _suppressionFailsafeTimer.Start();
        };
    }

    private void EnterMode(bool wantEmbedded)
    {
        LeaveCurrentMode();
        if (wantEmbedded)
        {
            _surface.SetEmbedded(true);
            _surface.ApplyAppearance();
            if (_nativeWidget.TryCreate())
            {
                _embedded = true;
                
                // Wire up embedded widget clicks
                _nativeWidget.LeftClicked += ShowDashboard;
                _nativeWidget.RightClicked += () => _trayIcon?.ContextMenuStrip?.Show(System.Windows.Forms.Cursor.Position);

                _nativeWidget.Reposition();
                _nativeWidget.EnsureTopAmongSiblings();
                _nativeRepositionTimer.Start();
                _guard?.Dispose();
                _guard = null;
                Visibility = Visibility.Hidden;
                return;
            }
            AppLog.Write("Embedded mode unavailable - falling back to the floating widget.");
        }
        _embedded = false;
        _surface.SetEmbedded(false);
        try
        {
            SurfaceHost.Content = _surface;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not re-host the widget surface: {ex.Message}");
            System.Windows.MessageBox.Show(
                "GeeTM needs to restart to finish switching display mode.",
                "GeeTM", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        _surface.ApplyAppearance();
        Visibility = Visibility.Visible;
        _guard ??= new OverlayGuardService(this);
        _guard.Resume();
        _guard.Start(GetHandle());
        _redockTimer.Start();
        DockOrPosition();
    }

    private void LeaveCurrentMode()
    {
        _redockTimer.Stop();
        _nativeRepositionTimer.Stop();
        _guard?.Suspend();
        if (_embedded)
        {
            _nativeWidget.Dispose();
            _embedded = false;
        }
        SurfaceHost.Content = null;
    }

    private void RepositionForMode()
    {
        if (_embedded) { _nativeWidget.Reposition(); _nativeWidget.EnsureTopAmongSiblings(); }
        else DockOrPosition();
    }

    private void RestoreFromSuppression()
    {
        if (!_suppressTopmost) return;
        _suppressTopmost = false;
        Visibility = Visibility.Visible;
        DockOrPosition();
        _guard?.Resume();
        _guard?.VerifyAndHeal(force: true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            AppLog.Write("Explorer restarted (TaskbarCreated) - re-attaching.");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Visible = true; }
                if (_embedded)
                {
                    _nativeWidget.Reposition();
                    _nativeWidget.EnsureTopAmongSiblings();
                }
                else
                {
                    _dockAnchorRight = null;
                    DockOrPosition();
                    _guard?.VerifyAndHeal(force: true);
                }
            }));
        }
        return IntPtr.Zero;
    }

    private IntPtr GetHandle() => new WindowInteropHelper(this).Handle;

    private void StartMonitoring()
    {
        _monitor.SampleReady += sample =>
        {
            _usage.Accumulate(sample);
            _history.Accumulate(sample);
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _surface.Update(sample, _usage.Totals);
                    if (_embedded) _nativeWidget.UpdateData(sample, _usage.Totals);
                    if (_overlay != null && _overlay.IsVisible) _overlay.UpdateSpeed(sample);
                    if (_trayIcon != null)
                    {
                        _trayIcon.Text = $"GeeTM \u2014 \u2193 {UnitFormatter.SpeedString(sample.DownloadBytesPerSec)}"
                                       + $"  \u2191 {UnitFormatter.SpeedString(sample.UploadBytesPerSec)}";
                    }
                    _dashboard?.PushSample(sample, _usage.Totals);
                }));
            }
            catch (Exception ex)
            {
                AppLog.Write($"UI update skipped: {ex.Message}");
            }
        };
        _usage.LimitExceeded += totals => Dispatcher.BeginInvoke(new Action(() =>
        {
            var s = SettingsService.Current;
            long today = totals.BytesReceivedToday + totals.BytesSentToday;
            long month = totals.BytesReceivedMonth + totals.BytesSentMonth;
            bool monthly = s.MonthlyLimitBytes > 0 && month > s.MonthlyLimitBytes;
            _trayIcon?.ShowBalloonTip(5000,
                monthly ? "GeeTM \u2014 Monthly limit reached" : "GeeTM \u2014 Daily limit reached",
                monthly ? $"This month: {UnitFormatter.TotalString(month)}" : $"Today: {UnitFormatter.TotalString(today)}",
                ToolTipIcon.Warning);
        }));
        _processMonitor.Updated += list =>
        {
            _dataCaps.Accumulate(list);
            Dispatcher.BeginInvoke(new Action(() => _dashboard?.PushProcessList(list)));
        };
        _dataCaps.CapExceeded += (processName, used, cap) => Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _trayIcon?.ShowBalloonTip(5000,
                    "GeeTM \u2014 App data cap reached",
                    $"{processName} has used {UnitFormatter.TotalString(used)} today (cap: {UnitFormatter.TotalString(cap)}).",
                    ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                AppLog.Write($"Data cap notification handler failed: {ex.Message}");
            }
        }));
        _vpnDetector.VpnStateChanged += connected => Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _trayIcon?.ShowBalloonTip(4000,
                    connected ? "GeeTM \u2014 VPN detected" : "GeeTM \u2014 VPN disconnected",
                    connected ? "A VPN adapter just became active." : "The VPN adapter is no longer active.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                AppLog.Write($"VPN notification handler failed: {ex.Message}");
            }
        }));
        if (SettingsService.Current.VpnNotificationsEnabled) _vpnDetector.Start();
        _monitor.Start();
    }

    private double? _dockAnchorRight;
    private double? _dockAnchorMidY;
    private double DpiScale
    {
        get
        {
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
    }

    private void DockOrPosition()
    {
        if (_embedded) return;
        var s = SettingsService.Current;
        if (s.WidgetManualPosition)
        {
            _dockAnchorRight = null;
            _dockAnchorMidY = null;
            double dpi = DpiScale;
            MoveToPhysical((int)Math.Round((s.WidgetX + s.WidgetOffsetX) * dpi),
                           (int)Math.Round((s.WidgetY - s.WidgetOffsetY) * dpi));
            return;
        }
        DockBesideTray();
    }

    private void DockBesideTray()
    {
        try
        {
            IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
            IntPtr notifyWnd = trayWnd != IntPtr.Zero
                ? FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null)
                : IntPtr.Zero;
            if (notifyWnd != IntPtr.Zero && GetWindowRect(notifyWnd, out var tray) && GetSelfRect(out var self))
            {
                var s = SettingsService.Current;
                double dpi = DpiScale;
                double w = self.Right - self.Left;
                double h = self.Bottom - self.Top;
                double rightEdge = tray.Left - (4 * dpi) + (s.WidgetOffsetX * dpi);
                double midY = ((tray.Top + tray.Bottom) / 2.0) - (s.WidgetOffsetY * dpi);
                _dockAnchorRight = rightEdge;
                _dockAnchorMidY = midY;
                MoveToPhysical((int)Math.Round(rightEdge - w), (int)Math.Round(midY - (h / 2)));
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"DockBesideTray failed, using fallback position: {ex.Message}");
        }
        try
        {
            if (!GetSelfRect(out var self)) return;
            var area = System.Windows.SystemParameters.WorkArea;
            double dpi = DpiScale;
            var fs = SettingsService.Current;
            double w = self.Right - self.Left;
            double h = self.Bottom - self.Top;
            _dockAnchorRight = (area.Right * dpi) - (160 * dpi) + (fs.WidgetOffsetX * dpi);
            _dockAnchorMidY = (area.Bottom * dpi) - (h / 2) - (4 * dpi) - (fs.WidgetOffsetY * dpi);
            MoveToPhysical((int)Math.Round(_dockAnchorRight.Value - w),
                           (int)Math.Round(_dockAnchorMidY.Value - (h / 2)));
        }
        catch (Exception ex)
        {
            AppLog.Write($"DockBesideTray fallback failed: {ex.Message}");
        }
    }

    private void MoveToPhysical(int x, int y)
    {
        var handle = GetHandle();
        if (handle == IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var cur)) return;
        if (cur.Left == x && cur.Top == y) return;
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
    }

    private bool GetSelfRect(out RECT rect)
    {
        rect = default;
        var handle = GetHandle();
        return handle != IntPtr.Zero && GetWindowRect(handle, out rect);
    }

    private void OnSurfaceResized()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_embedded) { _nativeWidget.Reposition(); return; }
            if (!_dockAnchorRight.HasValue || !_dockAnchorMidY.HasValue) return;
            if (!GetSelfRect(out var self)) return;
            double w = self.Right - self.Left;
            double h = self.Bottom - self.Top;
            MoveToPhysical((int)Math.Round(_dockAnchorRight.Value - w),
                           (int)Math.Round(_dockAnchorMidY.Value - (h / 2)));
        }));
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private void ApplySpecialWindowStyles()
    {
        try
        {
            var handle = GetHandle();
            if (handle == IntPtr.Zero) return;
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch (Exception ex)
        {
            AppLog.Write($"ApplySpecialWindowStyles failed: {ex.Message}");
        }
    }

    private bool? _lastClickThrough;
    private void SetClickThrough(bool enabled)
    {
        if (_lastClickThrough == enabled) return;
        try
        {
            var handle = GetHandle();
            if (handle == IntPtr.Zero) return;
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE,
                enabled ? (style | WS_EX_TRANSPARENT | WS_EX_LAYERED) : (style & ~WS_EX_TRANSPARENT));
            _lastClickThrough = enabled;
        }
        catch (Exception ex)
        {
            AppLog.Write($"SetClickThrough({enabled}) failed: {ex.Message}");
        }
    }

    private void SetupTrayIcon()
    {
        System.Drawing.Icon icon;
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "geetm.ico");
            icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Tray icon load failed, using system default: {ex.Message}");
            icon = System.Drawing.SystemIcons.Application;
        }
        _trayIcon = new NotifyIcon { Icon = icon, Visible = true, Text = "GeeTM" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Choose Adapter", null, (_, _) => ShowAdapterPicker());
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowDashboard();
    }

    private void ShowAdapterPicker()
    {
        var picker = new AdapterPickerWindow();
        picker.AdapterChosen += chosen => _monitor.PreferredAdapterName = chosen;
        picker.ShowDialog();
    }

    private void ShowSettings()
    {
        bool wasEmbedded = SettingsService.Current.EmbedInTaskbar;
        var settingsWindow = new SettingsWindow();
        settingsWindow.SettingsSaved += () =>
        {
            var s = SettingsService.Current;
            _monitor.PollIntervalMs = s.PollIntervalMs;
            _monitor.PreferredAdapterName = s.PreferredAdapter;
            if (s.ShowPerProcessBreakdown && !_processMonitor.IsAvailable) _processMonitor.Start();
            if (s.VpnNotificationsEnabled) _vpnDetector.Start(); else _vpnDetector.Stop();
            if (s.EmbedInTaskbar != wasEmbedded)
            {
                wasEmbedded = s.EmbedInTaskbar;
                EnterMode(s.EmbedInTaskbar);
            }
            else
            {
                _surface.ApplyAppearance();
                SetClickThrough(s.WidgetClickThrough);
                if (_embedded)
                {
                    _nativeWidget.ApplyFont();
                    _nativeWidget.RefreshColors();
                }
                _dockAnchorRight = null; 
                RepositionForMode();
                _guard?.VerifyAndHeal(force: true);
            }
            _dashboard?.RefreshTheme();
        };
        settingsWindow.ShowDialog();
    }

    private void ShowDashboard()
    {
        _dashboard ??= new Dashboard();
        _dashboard.HistoryService = _history;
        _dashboard.DataCapService = _dataCaps;
        _dashboard.OnSettingsRequested += ShowSettings;
        _dashboard.OnAdapterRequested += ShowAdapterPicker;
        _dashboard.OnExitRequested += Shutdown;
        _dashboard.Show();
        _dashboard.Activate();
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseEventArgs e) => ShowDashboard();
    private void Surface_MouseRightButtonUp(object sender, MouseEventArgs e)
        => _trayIcon?.ContextMenuStrip?.Show(System.Windows.Forms.Cursor.Position);
        
    private bool _isExiting;
    private void Shutdown()
    {
        _isExiting = true;
        _redockTimer.Stop();
        _colorRefreshTimer.Stop();
        _suppressionFailsafeTimer.Stop();
        _monitor.Dispose();
        _processMonitor.Dispose();
        _usage.Flush(); 
        _history.Flush();
        _dataCaps.Flush();
        _guard?.Dispose();
        _nativeWidget.Dispose();
        _fullscreenDetector.Dispose();
        _vpnDetector.Dispose();
        try { _overlay?.Close(); } catch { /* best-effort teardown */ }
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            AppLog.Write("Widget close attempt ignored - use tray icon Exit to quit GeeTM.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern uint RegisterWindowMessage(string lpString);
}



