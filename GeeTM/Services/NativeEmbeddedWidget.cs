using System.Collections.Generic;
using System.Runtime.InteropServices;
using GeeTM.Models;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace GeeTM.Services;

public sealed class NativeEmbeddedWidget : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _trayWnd;
    private IntPtr _notifyWnd;
    private readonly WndProcDelegate _wndProcDelegate;
    private bool _disposed;
    private const string ClassName = "GeeTMNativeEmbeddedWidget";
    private static ushort _registeredAtom;

    public bool IsCreated => _hwnd != IntPtr.Zero;

    // Interactivity Events
    public event Action? LeftClicked;
    public event Action? RightClicked;

    public NativeEmbeddedWidget()
    {
        _wndProcDelegate = WndProc;
    }

    public bool TryCreate()
    {
        try
        {
            _disposed = false;
            _loggedFirstReposition = false;
            _repositionFailLogCount = 0;
            _paintCount = 0;
            _winW = 0; _winH = 0;

            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            _trayWnd = FindWindow("Shell_TrayWnd", null);
            if (_trayWnd == IntPtr.Zero)
            {
                AppLog.Write("NativeEmbeddedWidget: could not find Shell_TrayWnd.");
                return false;
            }
            _notifyWnd = FindWindowEx(_trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
            RegisterWindowClassIfNeeded();

            _hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_LAYERED,
                ClassName, "",
                WS_POPUP | WS_CLIPSIBLINGS,
                0, 0, 120, 32,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                AppLog.Write($"NativeEmbeddedWidget: CreateWindowEx failed, error={Marshal.GetLastWin32Error()}.");
                return false;
            }

            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);

            SetParent(_hwnd, _trayWnd);
            int setParentError = Marshal.GetLastWin32Error();

            if (setParentError != 0)
            {
                AppLog.Write($"NativeEmbeddedWidget: SetParent failed once, error={setParentError} - retrying immediately.");
                SetParent(_hwnd, _trayWnd);
                setParentError = Marshal.GetLastWin32Error();
            }

            if (setParentError != 0)
            {
                AppLog.Write($"NativeEmbeddedWidget: SetParent genuinely failed after retry, error={setParentError} - aborting this embed attempt.");
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return false;
            }

            IntPtr ownerViaGetWindow = GetWindow(_hwnd, GW_OWNER);
            IntPtr ownerViaGetParent = GetParent(_hwnd);
            AppLog.Write($"NativeEmbeddedWidget: SetParent call completed, error={setParentError}. "
                       + $"GetWindow(GW_OWNER)=0x{ownerViaGetWindow.ToInt64():X} GetParent=0x{ownerViaGetParent.ToInt64():X} "
                       + $"(expected trayWnd=0x{_trayWnd.ToInt64():X}). Proceeding regardless of match.");

            ApplyFont(); 

            ShowWindow(_hwnd, SW_SHOW); 

            GetWindowRect(_hwnd, out var initialRect);
            bool initiallyVisible = IsWindowVisible(_hwnd);
            AppLog.Write($"NativeEmbeddedWidget: created. hwnd=0x{_hwnd.ToInt64():X} "
                       + $"rect=({initialRect.Left},{initialRect.Top},{initialRect.Right},{initialRect.Bottom}) "
                       + $"IsWindowVisible={initiallyVisible}.");
            LogTaskbarSiblings();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.TryCreate failed: {ex.Message}");
            return false;
        }
    }

    private void RegisterWindowClassIfNeeded()
    {
        if (_registeredAtom != 0) return;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _wndProcDelegate,
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName
        };
        _registeredAtom = RegisterClassEx(ref wc);
        if (_registeredAtom == 0)
        {
            AppLog.Write($"NativeEmbeddedWidget: RegisterClassEx failed, error={Marshal.GetLastWin32Error()}.");
        }
    }

    private string _upValue = "0.00", _upUnit = "KB/s";
    private string _downValue = "0.00", _downUnit = "KB/s";
    private string _todayValue = "0.0", _todayUnit = "MB";
    private string _todayLabel = "TODAY: ";
    // Speed pod's rotated content, when it's showing IP/Flag instead of its
    // normal two-row up/down layout.
    private string _speedRotLabel = "", _speedRotValue = "", _speedRotUnit = "";
    private bool _speedRotated, _todayRotated;

    public void UpdateData(NetSample sample, UsageTotals totals)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return; // IsWindow catches a handle Windows itself invalidated (e.g. Explorer recreating the taskbar during a display/power transition) - _hwnd alone only reflects what WE last set it to
        var (downVal, downUnit) = UnitFormatter.Speed(sample.DownloadBytesPerSec);
        var (upVal, upUnit) = UnitFormatter.Speed(sample.UploadBytesPerSec);
        _downValue = downVal; _downUnit = downUnit;
        _upValue = upVal; _upUnit = upUnit;

        var s = SettingsService.Current;
        long counted = s.TodayShowsMonth
            ? totals.BytesReceivedMonth + totals.BytesSentMonth
            : totals.BytesReceivedToday + totals.BytesSentToday;
        var (totalVal, totalUnit) = UnitFormatter.Total(counted);
        _todayValue = totalVal; _todayUnit = totalUnit;

        Resize();
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // v5.0 rotating pill: overrides the Today pill's label/value/unit fields
    // with IP or country-code content when it's their turn in the rotation.
    // Deliberately intercepts right here, before the existing measure/render
    // pipeline runs, rather than touching Resize() or the paint loop - those
    // already correctly measure and draw whatever these three fields
    // contain, so feeding them different text is the whole change needed.
    // Handles both pods: Today (unchanged from before) and Speed (new -
    // temporarily shows a single centered line instead of its normal
    // two-row up/down layout, exactly like Today's shape, while rotated
    // content is active). Wrapped in its own try/catch: this is newer code
    // than the rest of the rendering pipeline, and a failure here should
    // degrade to normal content for one frame, not risk the wider Resize()/
    // Paint() call that the "never crashes" promise depends on.
    private void ApplyRotatingPillContent(AppSettings s)
    {
        try
        {
            _speedRotated = false;
            _todayRotated = false;

            var todayState = RotatingPillHelper.GetCurrent(s, "Today");
            if (todayState != RotatingPillHelper.PillContent.Base)
            {
                var info = PublicIpService.GetCached();
                if (TryResolveContent(todayState, info, out var label, out var value))
                {
                    _showLabel = true; // label is drawn unconditionally downstream - width measurement must account for it
                    _todayLabel = label;
                    _todayValue = value;
                    _todayUnit = "";
                    _todayRotated = true;
                }
                // else: not resolved yet - keep showing normal Today content, already set above
            }

            var speedState = RotatingPillHelper.GetCurrent(s, "Speed");
            if (speedState != RotatingPillHelper.PillContent.Base)
            {
                var info = PublicIpService.GetCached();
                if (TryResolveContent(speedState, info, out var label, out var value))
                {
                    _speedRotLabel = label;
                    _speedRotValue = value;
                    _speedRotUnit = "";
                    _speedRotated = true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.ApplyRotatingPillContent failed: {ex.Message}");
            _speedRotated = false;
            _todayRotated = false;
        }

        // Fade trigger kept outside the main try/catch above and in its own
        // guarded call - a problem here should never be able to affect the
        // actual pill content logic, only the transition polish on top of it.
        try
        {
            if (s.EmbeddedFadeTransitionEnabled &&
                (RotatingPillHelper.JustChanged(s, "Today") || RotatingPillHelper.JustChanged(s, "Speed")))
            {
                TriggerFadeBlink();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.ApplyRotatingPillContent fade trigger failed: {ex.Message}");
        }
    }

    private static bool TryResolveContent(RotatingPillHelper.PillContent state, PublicIpService.IpInfo info, out string label, out string value)
    {
        label = ""; value = "";
        if (state == RotatingPillHelper.PillContent.Ip)
        {
            if (string.IsNullOrEmpty(info.Ip)) return false;
            // Threat score folds into the LABEL, not the value row - the IP
            // address text already dictates the pill's width in virtually
            // every case, so this never makes the pill wider on its own.
            label = "IP:" + ThreatCheckService.GetLabelSuffix(info.Ip) + " ";
            value = info.Ip;
            return true;
        }
        if (state == RotatingPillHelper.PillContent.Flag)
        {
            if (string.IsNullOrEmpty(info.CountryCode)) return false;
            // Plain country-code text, not flag emoji - reliable in both
            // floating (DirectWrite) and embedded (GDI+) rendering.
            label = "LOCATION: ";
            value = info.CountryCode;
            return true;
        }
        return false;
    }

    private IntPtr _hDigits, _hUnits, _hGlyph;
    private IntPtr _hTodayLabel, _hTodayValue, _hTodayUnit;
    private readonly List<IntPtr> _fontHandles = new();

    public void ApplyFont()
    {
        try
        {
            var s = SettingsService.Current;
            double dpi = GetDpiScale();

            foreach (var h in _fontHandles) DeleteObject(h);
            _fontHandles.Clear();

            string family = string.IsNullOrWhiteSpace(s.WidgetFontFamily) ? "Segoe UI" : s.WidgetFontFamily;
            _hDigits = MakeFont(family, s.WidgetFontSize, s.WidgetDigitsBold, dpi);
            _hUnits = MakeFont(family, s.WidgetFontSize, s.WidgetUnitBold, dpi);
            _hGlyph = MakeFont(family, s.WidgetFontSize, false, dpi);
            _hTodayLabel = MakeFont(family, s.TodayFontSize, false, dpi);
            _hTodayValue = MakeFont(family, s.TodayFontSize, s.TodayDigitsBold, dpi);
            _hTodayUnit = MakeFont(family, s.TodayFontSize, s.TodayUnitBold, dpi);

            AppLog.Write($"NativeEmbeddedWidget.ApplyFont: family='{family}' size={s.WidgetFontSize:0.#} "
                       + $"todaySize={s.TodayFontSize:0.#} dpi={dpi:0.##} digitsBold={s.WidgetDigitsBold} unitBold={s.WidgetUnitBold}.");

            Resize();
            ApplyExtras();
            if (_hwnd != IntPtr.Zero) InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.ApplyFont failed: {ex.Message}");
        }
    }

    private IntPtr MakeFont(string family, double sizeDip, bool semi, double dpi)
    {
        var lf = new LOGFONT
        {
            lfHeight = -(int)Math.Max(6, Math.Round(sizeDip * dpi)),
            lfWeight = semi ? FW_SEMIBOLD : FW_NORMAL,
            lfCharSet = DEFAULT_CHARSET,
            lfQuality = CLEARTYPE_QUALITY,
            lfFaceName = family
        };
        IntPtr h = CreateFontIndirect(ref lf);
        _fontHandles.Add(h);
        return h;
    }

    public void ApplyExtras()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return; // IsWindow catches a handle Windows itself invalidated (e.g. Explorer recreating the taskbar during a display/power transition) - _hwnd alone only reflects what WE last set it to
        try
        {
            var s = SettingsService.Current;

            byte alpha = (byte)Math.Round(255 * Math.Clamp(s.WidgetOpacity, 0, 1));
            SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);

            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex = s.WidgetClickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            AppLog.Write($"NativeEmbeddedWidget.ApplyExtras: opacity={s.WidgetOpacity:0.##} (alpha={alpha}) "
                       + $"clickThrough={s.WidgetClickThrough}.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.ApplyExtras failed: {ex.Message}");
        }
    }

    public void RefreshColors()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return; // IsWindow catches a handle Windows itself invalidated (e.g. Explorer recreating the taskbar during a display/power transition) - _hwnd alone only reflects what WE last set it to
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private float _dpiF = 1f;
    private int _speedW, _speedH, _gap, _totalW, _winW, _winH;
    private int _radius, _padH, _padV, _duGap, _tduGap, _rowGap, _iconGap;
    private int _speedX, _totalX;
    private bool _showToday, _showLabel;

    private sealed class RowInfo
    {
        public string Glyph = "";
        public string Val = "";
        public string Unit = "";
        public Color GlyphColor;
    }
    private readonly List<RowInfo> _rows = new();

    private int MeasureWidth(IntPtr dc, IntPtr font, string text)
    {
        IntPtr old = SelectObject(dc, font);
        GetTextExtentPoint32(dc, text, text.Length, out var sz);
        if (old != IntPtr.Zero) SelectObject(dc, old);
        return sz.cx;
    }

    private void Resize()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return; // IsWindow catches a handle Windows itself invalidated (e.g. Explorer recreating the taskbar during a display/power transition) - _hwnd alone only reflects what WE last set it to
        try
        {
            var s = SettingsService.Current;
            double dpi = GetDpiScale();
            _dpiF = (float)dpi;

            _showToday = s.ShowTodayInWidget;
            _showLabel = !string.IsNullOrEmpty(s.TodayLabelText);
            _todayLabel = _showLabel ? s.TodayLabelText.ToUpperInvariant() : "";
            ApplyRotatingPillContent(s); // may override _todayLabel/_todayValue/_todayUnit above - must run before the width math below

            _speedW = (int)Math.Round(Math.Clamp(s.WidgetWidth, 70, 400) * dpi);
            _speedH = (int)Math.Round(Math.Clamp(s.WidgetHeight, 18, 120) * dpi);
            _radius = (int)Math.Round(Math.Clamp(s.WidgetCornerRadius, 0, 20) * dpi);
            _padH = (int)Math.Round(s.WidgetPaddingH * dpi);
            _padV = (int)Math.Round(s.WidgetPaddingV * dpi);
            _duGap = (int)Math.Round(s.WidgetDigitUnitGap * dpi);
            _tduGap = (int)Math.Round(s.TodayDigitUnitGap * dpi);
            _rowGap = (int)Math.Round(s.WidgetRowGap * dpi);
            _iconGap = (int)Math.Round(s.WidgetIconTextGap * dpi);
            _gap = _showToday ? (int)Math.Round(s.WidgetGroupGap * dpi) : 0;

            bool showUp = s.ShowUploadRow || !s.ShowDownloadRow;   
            bool showDown = s.ShowDownloadRow || !s.ShowUploadRow;
            _rows.Clear();
            if (showUp) _rows.Add(new RowInfo { Glyph = "\u2191", Val = _upValue, Unit = _upUnit, GlyphColor = ThemeColor("UpBrush", "#FB923C") });
            if (showDown) _rows.Add(new RowInfo { Glyph = "\u2193", Val = _downValue, Unit = _downUnit, GlyphColor = ThemeColor("DownBrush", "#38BDF8") });

            IntPtr dc = GetDC(_hwnd);
            int lsW = 0, tvW = 0, tuW = 0;
            int speedLsW = 0, speedTvW = 0;
            if (dc != IntPtr.Zero)
            {
                try
                {
                    lsW = _showLabel ? MeasureWidth(dc, _hTodayLabel, _todayLabel) : 0;
                    tvW = MeasureWidth(dc, _hTodayValue, _todayValue);
                    tuW = MeasureWidth(dc, _hTodayUnit, _todayUnit);
                    if (_speedRotated)
                    {
                        // Reuses the Today pod's label/value fonts for
                        // consistency - the rotated content looks the same
                        // regardless of which pod it lands on.
                        speedLsW = MeasureWidth(dc, _hTodayLabel, _speedRotLabel);
                        speedTvW = MeasureWidth(dc, _hTodayValue, _speedRotValue);
                    }
                }
                finally
                {
                    ReleaseDC(_hwnd, dc); 
                }
            }
            int todayRowW = tvW + _tduGap + tuW;

            _totalW = _showToday
                ? Math.Max((int)Math.Round(40 * dpi),
                           (int)Math.Round(2 * s.TodayPaddingH * dpi) + Math.Max(lsW, todayRowW))
                : 0;

            if (_speedRotated)
            {
                // Same dynamic-sizing approach as the Today pod: fit exactly
                // what's being shown, ignoring the fixed WidgetWidth setting
                // for these frames only. Reverts to the normal fixed width
                // the instant rotation moves back to Base content.
                _speedW = Math.Max((int)Math.Round(40 * dpi),
                                    (int)Math.Round(2 * s.TodayPaddingH * dpi) + Math.Max(speedLsW, speedTvW));
            }

            _speedX = (s.TotalBeforeSpeed && _showToday) ? _gap + _totalW : 0;
            _totalX = s.TotalBeforeSpeed ? 0 : _speedW + _gap;

            int winW = _speedW + (_showToday ? _gap + _totalW : 0);
            int winH = _speedH;

            if (winW != _winW || winH != _winH)
            {
                _winW = winW;
                _winH = winH;
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _winW, _winH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                Reposition();
            }

            ApplyRegion();
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.Resize failed: {ex.Message}");
        }
    }

    /// <summary>Builds the region for one pod's shape. When both sides are
    /// rounded this is just a plain rounded rect. When one side isn't, that
    /// corner pair is "squared off" by unioning in a plain rectangle over
    /// where the rounding would otherwise be - this is what makes two pods
    /// read as one shape divided by the gap between them (PillShapeStyle ==
    /// "OnePod"): the facing/inner side of each pod gets square corners,
    /// the outer side keeps the normal rounding.</summary>
    private static IntPtr BuildPodRegion(int x, int y, int w, int h, int cornerDiameter, bool roundLeft, bool roundRight)
    {
        IntPtr current = CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, cornerDiameter, cornerDiameter);
        int radius = cornerDiameter / 2;

        if (!roundLeft)
        {
            IntPtr squareLeft = CreateRectRgn(x, y, x + radius, y + h + 1);
            IntPtr combined = CreateRectRgn(0, 0, 1, 1);
            CombineRgn(combined, current, squareLeft, RGN_OR);
            DeleteObject(current);
            DeleteObject(squareLeft);
            current = combined;
        }
        if (!roundRight)
        {
            IntPtr squareRight = CreateRectRgn(x + w - radius, y, x + w + 1, y + h + 1);
            IntPtr combined = CreateRectRgn(0, 0, 1, 1);
            CombineRgn(combined, current, squareRight, RGN_OR);
            DeleteObject(current);
            DeleteObject(squareRight);
            current = combined;
        }
        return current;
    }

    /// <summary>Draws one pod: its fill, and its border if enabled. When a
    /// border is enabled, this fills the OUTER (possibly asymmetric-corner)
    /// shape with the border colour first, then fills an INSET copy of the
    /// same shape with the pod colour on top - inset by the border thickness
    /// on every side except the inner/facing one (if any), where the inset
    /// is zero. That's what leaves the inner side without a visible border
    /// ring: the inner and outer shapes coincide exactly there.</summary>
    private static void DrawPod(IntPtr hdc, int x, int y, int w, int h, int cornerDiameter, bool roundLeft, bool roundRight, IntPtr podBrush, IntPtr borderBrush, int borderPx)
    {
        if (borderBrush == IntPtr.Zero)
        {
            IntPtr region = BuildPodRegion(x, y, w, h, cornerDiameter, roundLeft, roundRight);
            FillRgn(hdc, region, podBrush);
            DeleteObject(region);
            return;
        }

        IntPtr outer = BuildPodRegion(x, y, w, h, cornerDiameter, roundLeft, roundRight);
        FillRgn(hdc, outer, borderBrush);
        DeleteObject(outer);

        int leftInset = roundLeft ? borderPx : 0;
        int rightInset = roundRight ? borderPx : 0;
        int innerX = x + leftInset;
        int innerW = w - leftInset - rightInset;
        int innerY = y + borderPx;
        int innerH = h - 2 * borderPx;
        int innerDiameter = Math.Max(0, cornerDiameter - 2 * borderPx);

        if (innerW > 0 && innerH > 0)
        {
            IntPtr inner = BuildPodRegion(innerX, innerY, innerW, innerH, innerDiameter, roundLeft, roundRight);
            FillRgn(hdc, inner, podBrush);
            DeleteObject(inner);
        }
    }

    private void ApplyRegion()
    {
        if (_hwnd == IntPtr.Zero || _winW <= 0 || _winH <= 0) return;
        try
        {
            var s = SettingsService.Current;
            int d = Math.Max(2, _radius * 2);
            // Only meaningful with two pods present - with just one pod
            // there's nothing for it to visually merge with, so it stays
            // fully rounded regardless of the setting.
            bool onePodStyle = s.PillShapeStyle == "OnePod" && _showToday;
            bool speedIsLeft = !s.TotalBeforeSpeed;

            IntPtr r1 = onePodStyle
                ? BuildPodRegion(_speedX, 0, _speedW, _speedH, d, roundLeft: speedIsLeft, roundRight: !speedIsLeft)
                : CreateRoundRectRgn(_speedX, 0, _speedX + _speedW + 1, _speedH + 1, d, d);
            IntPtr combined = r1;
            if (_showToday)
            {
                IntPtr r2 = onePodStyle
                    ? BuildPodRegion(_totalX, 0, _totalW, _speedH, d, roundLeft: !speedIsLeft, roundRight: speedIsLeft)
                    : CreateRoundRectRgn(_totalX, 0, _totalX + _totalW + 1, _speedH + 1, d, d);
                combined = CreateRectRgn(0, 0, 1, 1);
                CombineRgn(combined, r1, r2, RGN_OR);
                DeleteObject(r1);
                DeleteObject(r2);
            }
            SetWindowRgn(_hwnd, combined, true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.ApplyRegion failed: {ex.Message}");
        }
    }

    private static uint Ref(Color c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    private static Color ThemeColor(string key, string fallbackHex)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
                return b.Color;
        }
        catch { }
        return SafeParseColor(fallbackHex);
    }

    private static Color SafeParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return Color.FromRgb(0x0E, 0x11, 0x16); }
    }

    private bool _loggedFirstReposition;
    private int _repositionFailLogCount;

    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return; // IsWindow catches a handle Windows itself invalidated (e.g. Explorer recreating the taskbar during a display/power transition) - _hwnd alone only reflects what WE last set it to
        try
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            if (!IsWindow(_trayWnd))
            {
                _trayWnd = FindWindow("Shell_TrayWnd", null);
                if (_trayWnd == IntPtr.Zero)
                {
                    AppLog.Write("NativeEmbeddedWidget.Reposition: Shell_TrayWnd not found.");
                    return;
                }
            }
            if (!IsWindow(_notifyWnd))
                _notifyWnd = FindWindowEx(_trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);

            if (!GetWindowRect(_hwnd, out var self)) return;
            int w = self.Right - self.Left, h = self.Bottom - self.Top;
            if (w <= 0 || h <= 0) return;

            double dpi = GetDpiScale();
            var s = SettingsService.Current;

            int screenX;
            int screenY;
            RECT anchor = default;
            if (s.WidgetManualPosition)
            {
                screenX = (int)Math.Round((s.WidgetX + s.WidgetOffsetX) * dpi);
                screenY = (int)Math.Round((s.WidgetY - s.WidgetOffsetY) * dpi);
            }
            else
            {
                if (_notifyWnd != IntPtr.Zero && GetWindowRect(_notifyWnd, out var notifyRect))
                {
                    anchor = notifyRect;
                }
                else
                {
                    if (!GetWindowRect(_trayWnd, out var trayRect)) return;
                    anchor = new RECT { Left = trayRect.Right, Top = trayRect.Top, Right = trayRect.Right, Bottom = trayRect.Bottom };
                }
                int anchorHeight = anchor.Bottom - anchor.Top;
                screenX = anchor.Left - w - (int)Math.Round(4 * dpi) + (int)Math.Round(s.WidgetOffsetX * dpi);
                screenY = anchor.Top + ((anchorHeight - h) / 2) - (int)Math.Round(s.WidgetOffsetY * dpi);
            }

            if (self.Left == screenX && self.Top == screenY)
            {
                if (!_loggedFirstReposition)
                {
                    _loggedFirstReposition = true;
                    AppLog.Write($"NativeEmbeddedWidget.Reposition: already at screen ({screenX},{screenY}) size {w}x{h}, dpi={dpi:0.##} - no move needed.");
                    LogTaskbarSiblings();
                }
                return;
            }

            if (!GetWindowRect(_trayWnd, out var trayRectForClient))
            {
                AppLog.Write("NativeEmbeddedWidget.Reposition: cannot read Shell_TrayWnd rect; aborting move.");
                return;
            }

            var p = new POINT { X = screenX, Y = screenY };
            bool clientConverted = ScreenToClient(_trayWnd, ref p);
            if (!clientConverted) { p.X = screenX; p.Y = screenY; }

            SetWindowPos(_hwnd, IntPtr.Zero, p.X, p.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            GetWindowRect(_hwnd, out var actualRect);
            bool landed = Math.Abs(actualRect.Left - screenX) <= 2 && Math.Abs(actualRect.Top - screenY) <= 2;

            if (!_loggedFirstReposition || (!landed && _repositionFailLogCount < 3))
            {
                if (landed) _loggedFirstReposition = true; else _repositionFailLogCount++;
                AppLog.Write($"NativeEmbeddedWidget.Reposition: requestedScreen=({screenX},{screenY}) "
                           + $"trayClient=({p.X},{p.Y}) clientConverted={clientConverted} "
                           + $"trayRect=({trayRectForClient.Left},{trayRectForClient.Top},{trayRectForClient.Right},{trayRectForClient.Bottom}) "
                           + $"anchor=({anchor.Left},{anchor.Top},{anchor.Right},{anchor.Bottom}) "
                           + $"actual=({actualRect.Left},{actualRect.Top},{actualRect.Right},{actualRect.Bottom}) landed={landed}.");
                if (landed) LogTaskbarSiblings();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.Reposition failed: {ex.Message}");
        }
    }

    private void LogTaskbarSiblings()
    {
        try
        {
            var sb = new System.Text.StringBuilder("NativeEmbeddedWidget siblings (top first): ");
            var child = GetWindow(_trayWnd, GW_CHILD);
            int guard = 0;
            while (child != IntPtr.Zero && guard++ < 24)
            {
                var cls = new System.Text.StringBuilder(160);
                GetClassNameNative(child, cls, cls.Capacity);
                GetWindowRect(child, out var cr);
                bool us = child == _hwnd;
                bool vis = IsWindowVisible(child);
                sb.Append($"[{(us ? "GeeTM->" : "")}{cls} rect=({cr.Left},{cr.Top},{cr.Right},{cr.Bottom}) vis={vis}] ");
                child = GetWindow(child, GW_HWNDNEXT);
            }
            AppLog.Write(sb.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.LogTaskbarSiblings failed: {ex.Message}");
        }
    }

    private double GetDpiScale()
    {
        try
        {
            uint dpi = GetDpiForWindow(_hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    public void EnsureTopAmongSiblings()
    {
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // This method is invoked directly by Windows as a native callback -
        // NOT through the WPF Dispatcher, NOT through a Task, NOT through any
        // of the managed exception safety nets in App.xaml.cs. An exception
        // escaping a native callback boundary is essentially always fatal:
        // the CLR cannot safely unwind through the native call frame, and the
        // whole process terminates immediately and unpredictably - exactly
        // the "crashes sometimes, no clear pattern" symptom this was causing.
        // LeftClicked/RightClicked run real subscriber code (opening the
        // Dashboard, building a context menu) that can legitimately throw for
        // many transient reasons; that must never be allowed to reach Windows
        // unguarded. Every path through this method is now caught here.
        try
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    return (IntPtr)1;
                case WM_PAINT:
                    Paint(hwnd);
                    return IntPtr.Zero;
                case 0x0201: // WM_LBUTTONDOWN
                    LeftClicked?.Invoke();
                    return IntPtr.Zero;
                case 0x0205: // WM_RBUTTONUP
                    RightClicked?.Invoke();
                    return IntPtr.Zero;
                case WM_NCDESTROY:
                    ReleaseFonts();
                    return DefWindowProc(hwnd, msg, wParam, lParam);
                default:
                    return DefWindowProc(hwnd, msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Last-resort net for the single most crash-prone boundary in the
            // app: log and return a safe default instead of letting anything
            // escape into native code.
            try { AppLog.Write($"NativeEmbeddedWidget.WndProc failed (msg=0x{msg:X}): {ex}"); } catch { /* logging itself must never throw here */ }
            return IntPtr.Zero;
        }
    }

    private int _paintCount;

    /// <summary>Draws a centered label above a centered value (no unit, for
    /// IP/location text) within the given pod rectangle - the same layout
    /// math the Today pod already uses for its normal content, extracted so
    /// either pod can use it when showing rotated content.</summary>
    private void DrawSingleLineContent(IntPtr hdc, string label, string value, string unit, int x, int w, Color textColor, Color mutedColor)
    {
        SelectObject(hdc, _hTodayLabel);
        GetTextMetrics(hdc, out var lm);
        SelectObject(hdc, _hTodayValue);
        GetTextMetrics(hdc, out var vm);

        int wV = MeasureWidth(hdc, _hTodayValue, value);
        int wU = string.IsNullOrEmpty(unit) ? 0 : MeasureWidth(hdc, _hTodayUnit, unit);

        int labelGap = (int)Math.Round(1 * _dpiF);
        int contentHt = lm.tmHeight + labelGap + vm.tmHeight;
        int top = (_speedH - contentHt) / 2;

        SetTextColor(hdc, Ref(mutedColor));
        SelectObject(hdc, _hTodayLabel);
        var lr = new RECT { Left = x, Top = top, Right = x + w, Bottom = top + lm.tmHeight };
        DrawTextW(hdc, label, label.Length, ref lr, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

        int yRow = top + lm.tmHeight + labelGap;
        int rowW = wV + (wU > 0 ? _tduGap + wU : 0);
        int x0 = x + (w - rowW) / 2;

        SetTextColor(hdc, Ref(textColor));
        SelectObject(hdc, _hTodayValue);
        var vr = new RECT { Left = x0, Top = yRow, Right = x0 + wV, Bottom = yRow + vm.tmHeight };
        DrawTextW(hdc, value, value.Length, ref vr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

        if (wU > 0)
        {
            SelectObject(hdc, _hTodayUnit);
            var ur = new RECT { Left = x0 + wV + _tduGap, Top = yRow, Right = x0 + rowW, Bottom = yRow + vm.tmHeight };
            DrawTextW(hdc, unit, unit.Length, ref ur, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);
        }
    }

    private void Paint(IntPtr hwnd)
    {
        IntPtr hdc = BeginPaint(hwnd, out var ps);
        try
        {
            GetClientRect(hwnd, out var rc);
            if (_paintCount++ < 5)
            {
                GetWindowRect(hwnd, out var screenRect);
                AppLog.Write($"NativeEmbeddedWidget.Paint #{_paintCount}: client=({rc.Left},{rc.Top},{rc.Right},{rc.Bottom}) "
                           + $"screen=({screenRect.Left},{screenRect.Top},{screenRect.Right},{screenRect.Bottom}) "
                           + $"visible={IsWindowVisible(hwnd)} upText='{_upValue} {_upUnit}' downText='{_downValue} {_downUnit}'.");
            }

            var s = SettingsService.Current;

            Color pod = s.ColorMode switch
            {
                WidgetColorMode.AutoExact => TaskbarColorService.GetTaskbarColor(),
                WidgetColorMode.AutoDarker => TaskbarColorService.Darken(TaskbarColorService.GetTaskbarColor(), s.AutoDarkenAmount),
                _ => SafeParseColor(s.WidgetBackgroundHex)
            };
            Color textC = ThemeColor("TextPrimaryBrush", "#F2F5F9");
            Color mutedC = ThemeColor("TextMutedBrush", "#7E8899");

            int d = Math.Max(2, _radius * 2);
            IntPtr podBrush = CreateSolidBrush(Ref(pod));
            IntPtr borderBrush = IntPtr.Zero;
            int borderPx = 0;
            if (s.PillBorderEnabled)
            {
                borderBrush = CreateSolidBrush(Ref(SafeParseColor(s.PillBorderColorHex)));
                borderPx = Math.Max(1, (int)Math.Round(s.PillBorderThickness * _dpiF));
            }

            bool onePodStyle = s.PillShapeStyle == "OnePod" && _showToday;
            bool speedIsLeft = !s.TotalBeforeSpeed;
            bool speedRoundLeft = !onePodStyle || speedIsLeft;
            bool speedRoundRight = !onePodStyle || !speedIsLeft;

            DrawPod(hdc, _speedX, 0, _speedW, _speedH, d, speedRoundLeft, speedRoundRight, podBrush, borderBrush, borderPx);
            if (_showToday)
            {
                bool totalRoundLeft = !onePodStyle || !speedIsLeft;
                bool totalRoundRight = !onePodStyle || speedIsLeft;
                DrawPod(hdc, _totalX, 0, _totalW, _speedH, d, totalRoundLeft, totalRoundRight, podBrush, borderBrush, borderPx);
            }
            DeleteObject(podBrush);
            if (borderBrush != IntPtr.Zero) DeleteObject(borderBrush);

            SetBkMode(hdc, TRANSPARENT_BKMODE);

            if (_speedRotated)
            {
                DrawSingleLineContent(hdc, _speedRotLabel, _speedRotValue, "", _speedX, _speedW, textC, mutedC);
            }
            else
            {
                int n = Math.Max(1, _rows.Count);
                int contentH = _speedH - 2 * _padV;
                int rowH = contentH / n;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    int rowTop = _padV + rowH * i;
                    int rowBottom = rowTop + rowH;
                    if (i == 0 && _rows.Count > 1) { rowTop -= _rowGap / 2; rowBottom -= _rowGap / 2; }

                    SetTextColor(hdc, Ref(row.GlyphColor));
                    SelectObject(hdc, _hGlyph);
                    var gr = new RECT { Left = _speedX + _padH, Top = rowTop, Right = _speedX + _speedW, Bottom = rowBottom };
                    DrawTextW(hdc, row.Glyph, row.Glyph.Length, ref gr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

                    int glyphW = MeasureWidth(hdc, _hGlyph, row.Glyph);
                    int wV = MeasureWidth(hdc, _hDigits, row.Val);
                    int wU = MeasureWidth(hdc, _hUnits, row.Unit);
                    int minX = _speedX + _padH + glyphW + _iconGap;
                    int x0 = Math.Max(minX, _speedX + _speedW - _padH - (wV + _duGap + wU));

                    SetTextColor(hdc, Ref(textC));
                    SelectObject(hdc, _hDigits);
                    var vr = new RECT { Left = x0, Top = rowTop, Right = _speedX + _speedW - _padH, Bottom = rowBottom };
                    DrawTextW(hdc, row.Val, row.Val.Length, ref vr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

                    SelectObject(hdc, _hUnits);
                    var ur = new RECT { Left = x0 + wV + _duGap, Top = rowTop, Right = _speedX + _speedW - _padH, Bottom = rowBottom };
                    DrawTextW(hdc, row.Unit, row.Unit.Length, ref ur, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);
                }
            }

            if (_showToday)
            {
                SelectObject(hdc, _hTodayLabel);
                GetTextMetrics(hdc, out var lm);
                SelectObject(hdc, _hTodayValue);
                GetTextMetrics(hdc, out var vm);

                int wTV = MeasureWidth(hdc, _hTodayValue, _todayValue);
                int wTU = MeasureWidth(hdc, _hTodayUnit, _todayUnit);

                int labelGap = (int)Math.Round(1 * _dpiF);
                int contentHt = lm.tmHeight + labelGap + vm.tmHeight;
                int top = (_speedH - contentHt) / 2;

                SetTextColor(hdc, Ref(mutedC));
                SelectObject(hdc, _hTodayLabel);
                var lr = new RECT { Left = _totalX, Top = top, Right = _totalX + _totalW, Bottom = top + lm.tmHeight };
                DrawTextW(hdc, _todayLabel, _todayLabel.Length, ref lr, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

                int yRow = top + lm.tmHeight + labelGap;
                int rowW = wTV + _tduGap + wTU;
                int x0 = _totalX + (_totalW - rowW) / 2;

                SetTextColor(hdc, Ref(textC));
                SelectObject(hdc, _hTodayValue);
                var tvr = new RECT { Left = x0, Top = yRow, Right = x0 + rowW, Bottom = yRow + vm.tmHeight };
                DrawTextW(hdc, _todayValue, _todayValue.Length, ref tvr, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);

                SelectObject(hdc, _hTodayUnit);
                var tur = new RECT { Left = x0 + wTV + _tduGap, Top = yRow, Right = x0 + rowW, Bottom = yRow + vm.tmHeight };
                DrawTextW(hdc, _todayUnit, _todayUnit.Length, ref tur, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.Paint failed: {ex.Message}");
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private void ReleaseFonts()
    {
        foreach (var h in _fontHandles) DeleteObject(h);
        _fontHandles.Clear();
        _hDigits = _hUnits = _hGlyph = IntPtr.Zero;
        _hTodayLabel = _hTodayValue = _hTodayUnit = IntPtr.Zero;
    }

    // --- Embedded-mode fade transition (opt-in, see EmbeddedFadeTransitionEnabled) ---
    // A brief opacity dip-and-recover using only SetLayeredWindowAttributes -
    // the same call ApplyExtras() already uses for normal opacity, just
    // driven by a short timer instead of a single fixed value. Deliberately
    // avoids anything more elaborate (no per-pixel blending, no new Win32
    // calls) given this is the highest-risk rendering path in the app.
    private System.Windows.Threading.DispatcherTimer? _fadeTimer;
    private int _fadeStep;
    private byte _fadeTargetAlpha = 255;
    private const int FadeTotalSteps = 10;

    private void TriggerFadeBlink()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return; // IsWindow catches a handle Windows itself invalidated (e.g. Explorer recreating the taskbar during a display/power transition) - _hwnd alone only reflects what WE last set it to
        try
        {
            var s = SettingsService.Current;
            // Floor at 30% opacity so the blink is visible but the widget
            // never fully disappears mid-transition.
            _fadeTargetAlpha = (byte)Math.Round(255 * Math.Clamp(s.WidgetOpacity, 0.3, 1.0));
            _fadeStep = 0;

            if (_fadeTimer == null)
            {
                _fadeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                _fadeTimer.Tick += FadeTimerTick;
            }
            _fadeTimer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.TriggerFadeBlink failed: {ex.Message}");
        }
    }

    private void FadeTimerTick(object? sender, EventArgs e)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) { _fadeTimer?.Stop(); return; }

            _fadeStep++;
            double t = _fadeStep / (double)FadeTotalSteps;
            // Dips to 30% of the target alpha at the midpoint, then recovers -
            // a soft "blink" rather than a hard cut.
            double factor = t <= 0.5 ? (1.0 - t * 2 * 0.7) : (0.3 + (t - 0.5) * 2 * 0.7);
            byte alpha = (byte)Math.Clamp(_fadeTargetAlpha * factor, 40, 255);
            SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);

            if (_fadeStep >= FadeTotalSteps)
            {
                _fadeTimer?.Stop();
                SetLayeredWindowAttributes(_hwnd, 0, _fadeTargetAlpha, LWA_ALPHA); // land on the exact target, not an interpolated value
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.FadeTimerTick failed: {ex.Message}");
            try { _fadeTimer?.Stop(); } catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _fadeTimer?.Stop();
            if (_fadeTimer != null) _fadeTimer.Tick -= FadeTimerTick;
            ReleaseFonts();
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        }
        catch (Exception ex)
        {
            AppLog.Write($"NativeEmbeddedWidget.Dispose failed: {ex.Message}");
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TEXTMETRIC
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading,
                   tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    private const uint WS_CLIPSIBLINGS = 0x04000000, WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000;
    private const int SW_SHOW = 5;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint GW_CHILD = 5, GW_OWNER = 4, GW_HWNDNEXT = 2;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    private const uint LWA_ALPHA = 0x2;
    private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_NCDESTROY = 0x0082;
    private const int TRANSPARENT_BKMODE = 1;
    private const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOCLIP = 0x100;
    private const int FW_NORMAL = 400, FW_SEMIBOLD = 600, DEFAULT_CHARSET = 1;
    private const byte CLEARTYPE_QUALITY = 5;
    private const int IDC_ARROW = 32512;
    private const int RGN_OR = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassNameNative(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontIndirectW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontIndirect(ref LOGFONT lplf);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint crColor);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(IntPtr hdc, IntPtr hrgn, IntPtr hbr);
    [DllImport("gdi32.dll")] private static extern bool FrameRgn(IntPtr hdc, IntPtr hrgn, IntPtr hbr, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint crColor);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr hdc, string lpString, int c, out SIZE psizl);
    [DllImport("gdi32.dll")] private static extern bool GetTextMetrics(IntPtr hdc, out TEXTMETRIC lptm);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int iMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hDc, string lpchText, int nCount, ref RECT lpRect, uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}


