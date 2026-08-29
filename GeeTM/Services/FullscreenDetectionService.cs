using System.Runtime.InteropServices;
using System.Text;

namespace GeeTM.Services;

/// <summary>
/// Detects genuine fullscreen content so the widget can stand down, without
/// mistaking shell surfaces for it.
///
/// The previous version had a serious false-positive hole: Windows 11 hosts
/// the Start menu, Quick Settings and Notification Center in XAML-island
/// windows that are frequently created at FULL MONITOR SIZE with most of the
/// surface transparent. Since only Progman/WorkerW/Shell_TrayWnd were
/// excluded, opening Start could satisfy the "bounds == monitor bounds" test
/// and collapse the widget outright ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â a second, independent cause of the
/// disappearance being reported, on top of the z-order bug.
///
/// Fixes here:
///  - Shell window classes and shell host processes are excluded outright.
///  - Cloaked windows never count (a cloaked window occupies no pixels).
///  - Exit is debounced too, so a single stray frame can't flap the state.
///  - IsFullscreenNow() is public so the widget's failsafe can ask the real
///    question instead of blindly un-suppressing after a fixed timeout.
/// </summary>
public class FullscreenDetectionService : IDisposable
{
    public event Action? FullscreenStarted;
    public event Action? FullscreenEnded;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Desktop and shell surfaces that can legitimately cover a whole
    /// monitor without being "an app in fullscreen". The Win11 XAML-island and
    /// CoreWindow entries are the ones that were missing, and they are exactly
    /// what the Start menu and Quick Settings flyouts use.</summary>
    private static readonly string[] IgnoredClassNames =
    {
        "Progman", "WorkerW",
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TrayNotifyWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "Xaml_WindowedPopupClass", "ControlCenterWindow",
        "Shell_InputSwitchTopLevelWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd",
        "Windows.UI.Composition.DesktopWindowContentBridge"
    };

    /// <summary>Processes that host shell UI. Their windows are never "an app
    /// gone fullscreen" no matter what size they report.</summary>
    private static readonly string[] IgnoredProcesses =
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "searchapp", "textinputhost", "applicationframehost",
        "systemsettings", "lockapp"
    };

    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _isFullscreen;
    private int _consecutiveMatches;
    private int _consecutiveMisses;
    private IntPtr _ownHandle;

    private const int RequiredConsecutiveMatches = 2; // ~1.6 s sustained before suppressing
    private const int RequiredConsecutiveMisses = 1;  // leave fullscreen promptly

    public FullscreenDetectionService()
    {
        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>The widget's own window handle must be excluded, or it would
    /// see itself as "not fullscreen" forever and never detect anything else.</summary>
    public void SetOwnHandle(IntPtr handle) => _ownHandle = handle;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>Live, un-debounced answer. The widget's failsafe uses this so
    /// it can only ever un-suppress when fullscreen has genuinely ended.</summary>
    public bool IsFullscreenNow()
    {
        try { return IsForegroundWindowFullscreen(); }
        catch { return false; }
    }

    private void Check()
    {
        try
        {
            bool matchesNow = IsForegroundWindowFullscreen();
            _consecutiveMatches = matchesNow ? _consecutiveMatches + 1 : 0;
            _consecutiveMisses = matchesNow ? 0 : _consecutiveMisses + 1;

            if (!_isFullscreen && _consecutiveMatches >= RequiredConsecutiveMatches)
            {
                _isFullscreen = true;
                AppLog.Write("Fullscreen detected (sustained match) ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â suppressing widget.");
                FullscreenStarted?.Invoke();
            }
            else if (_isFullscreen && _consecutiveMisses >= RequiredConsecutiveMisses)
            {
                _isFullscreen = false;
                AppLog.Write("Fullscreen ended ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â restoring widget.");
                FullscreenEnded?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"FullscreenDetectionService.Check failed: {ex.Message}");
        }
    }

    private bool IsForegroundWindowFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _ownHandle) return false;

        var classNameBuilder = new StringBuilder(256);
        GetClassName(fg, classNameBuilder, classNameBuilder.Capacity);
        if (Array.IndexOf(IgnoredClassNames, classNameBuilder.ToString()) >= 0) return false;

        if (IsShellHostProcess(fg)) return false;

        // A cloaked window is in the window list but paints nothing. Treating
        // one as fullscreen would hide the widget behind nothing at all.
        if (DwmGetWindowAttribute(fg, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        // Prefer DWM's extended frame bounds ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the actual visible window edge,
        // excluding the invisible resize border GetWindowRect includes for
        // maximized windows. Fall back only if the DWM call itself fails.
        RECT winRect;
        int hr = DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out winRect, Marshal.SizeOf<RECT>());
        if (hr != 0 && !GetWindowRect(fg, out winRect)) return false;

        IntPtr monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;

        return winRect.Left == mi.rcMonitor.Left
            && winRect.Top == mi.rcMonitor.Top
            && winRect.Right == mi.rcMonitor.Right
            && winRect.Bottom == mi.rcMonitor.Bottom;
    }

    private static bool IsShellHostProcess(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return Array.IndexOf(IgnoredProcesses, p.ProcessName.ToLowerInvariant()) >= 0;
        }
        catch { return false; }
    }

    public void Dispose() => _timer.Stop();
}



