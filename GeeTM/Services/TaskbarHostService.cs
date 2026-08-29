using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

// This project enables both UseWPF and UseWindowsForms, so implicit usings pull
// in System.Drawing alongside WPF, and Size exists in both. Alias directives
// take precedence over namespace imports, so pinning the WPF meaning once here
// avoids fully qualifying every use below.
using Size = System.Windows.Size;

namespace GeeTM.Services;

/// <summary>
/// Hosts the widget as a genuine child window of the Windows taskbar.
///
/// This is the technique TrafficMonitor uses, and it is why TrafficMonitor never
/// vanishes behind the Start menu or Quick Settings: a child of Shell_TrayWnd is
/// not competing for z-order with shell flyouts at all. It is part of the
/// taskbar, and the taskbar is never covered by its own flyouts. No amount of
/// topmost juggling on an ordinary window can reproduce that, because Windows 11
/// puts shell surfaces in a z-band above anything a normal app can reach.
///
/// The critical detail, and the reason the earlier attempt in this project
/// failed: you cannot call SetParent on an already-created WPF Window. WPF's
/// HwndSource is built assuming top-level operation and demoting it afterwards
/// breaks its rendering pipeline - exactly the "goes completely invisible"
/// symptom that was observed. The supported route is to create an HwndSource
/// with WS_CHILD and a parent HWND *from the start* and hand it a RootVisual.
/// Hosting WPF content in a child HWND is a documented interop scenario.
///
/// Known constraints, all handled below:
///  - Elevation is NOT a barrier. UIPI blocks a LOWER integrity process from
///    manipulating a HIGHER integrity window; Explorer runs at medium
///    integrity, so an elevated GeeTM is the higher one and may parent into it
///    freely. An unelevated GeeTM is at the same level as Explorer, which also
///    works. This is why TrafficMonitor embeds successfully while running as
///    administrator. Embedding is therefore always attempted.
///  - Explorer restarts destroy the parent and the child with it, so the host
///    re-embeds on TaskbarCreated / when the parent handle goes stale.
///  - No per-pixel transparency on a child window, so the surface paints an
///    opaque background instead of relying on a layered window.
/// </summary>
public sealed class TaskbarHostService : IDisposable
{
    private HwndSource? _source;
    private IntPtr _trayWnd = IntPtr.Zero;
    private IntPtr _notifyWnd = IntPtr.Zero;
    private UIElement? _content;
    private readonly DispatcherTimer _layoutTimer;
    private readonly DispatcherTimer _verifyTimer;
    private bool _disposed;
    private bool _loggedFirstLayout;
    private int _verifyAttempts;

    // The 1-second layout poll is far too slow to beat a shell repaint: opening
    // the Start menu redraws Windows.UI.Composition.DesktopWindowContentBridge
    // (the modern taskbar's own composited UI surface), and that surface can
    // paint back over our legacy child window even though nothing in the Win32
    // z-order actually changed. Reacting to the same shell events instead of
    // polling is what makes the re-assert land before the next frame is shown.
    private IntPtr _foregroundHook;
    private IntPtr _showHideHook;
    private readonly WinEventDelegate _eventCallback;

    public bool IsEmbedded => _source != null && _source.Handle != IntPtr.Zero;

    /// <summary>What actually happened on the last embed attempt, in plain
    /// language. Surfaced in Settings so the mode in effect is never a guess.</summary>
    public static string LastStatus { get; private set; } = "Not attempted yet.";

    /// <summary>Raised when the embed is lost and could not be re-established,
    /// so the caller can fall back to the floating window.</summary>
    public event Action? EmbedFailed;

    public TaskbarHostService()
    {
        // Explorer re-lays-out the tray whenever an icon appears, hides, or the
        // clock changes width, so the slot we occupy moves. One second is
        // frequent enough to track that without being noticeable work.
        _layoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _layoutTimer.Tick += (_, _) => Reconcile();

        // Fires once, a couple of seconds after the embed, to confirm the widget
        // genuinely reached the screen rather than merely being created.
        _verifyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _verifyTimer.Tick += (_, _) => { _verifyTimer.Stop(); VerifyVisible(); };

        _eventCallback = OnShellEvent;
    }

    private void InstallShellHooks()
    {
        if (_foregroundHook != IntPtr.Zero) return; // already installed

        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _eventCallback, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Narrow range rather than the full 0x8000-series: EVENT_OBJECT_SHOW and
        // EVENT_OBJECT_HIDE bracket exactly the shell-surface show/hide events we
        // care about (Start, Quick Settings, search) without also picking up
        // EVENT_OBJECT_LOCATIONCHANGE, which fires on every pixel of every
        // window drag system-wide.
        _showHideHook = SetWinEventHook(
            EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE,
            IntPtr.Zero, _eventCallback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void RemoveShellHooks()
    {
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_showHideHook != IntPtr.Zero) { UnhookWinEvent(_showHideHook); _showHideHook = IntPtr.Zero; }
    }

    private void OnShellEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_disposed || _source == null) return;
        // Re-assert immediately rather than waiting for the next poll tick - the
        // whole point is to win the race against the shell's own repaint.
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _source == null) return;
            EnsureTopAmongSiblings();
            ForceRepaint();
        }));
    }

    /// <summary>Forces the child to redraw immediately rather than waiting for
    /// the next natural paint pass, in case the composited bridge surface drew
    /// over our pixels without actually changing the Win32 z-order.</summary>
    private void ForceRepaint()
    {
        if (_source == null) return;
        RedrawWindow(_source.Handle, IntPtr.Zero, IntPtr.Zero,
            RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME);
    }

    /// <summary>
    /// Confirms the embedded child is actually on screen. A successful
    /// CreateWindowEx plus the right GetParent result is not sufficient
    /// evidence: on Windows 11 the taskbar composites its own XAML surface, and
    /// a plain child HWND underneath it is a perfectly valid, perfectly
    /// invisible window. If we cannot prove it is visible, fall back rather than
    /// leaving the user staring at an empty taskbar.
    /// </summary>
    private void VerifyVisible()
    {
        if (_disposed || _source == null) return;

        try
        {
            bool visible = IsWindowVisible(_source.Handle);
            GetWindowRect(_source.Handle, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            GetWindowRect(_trayWnd, out var tray);

            bool sized = w > 1 && h > 1;
            bool insideTaskbar = r.Left < tray.Right && r.Right > tray.Left
                              && r.Top < tray.Bottom && r.Bottom > tray.Top;
            bool onTop = GetWindow(_trayWnd, GW_CHILD) == _source.Handle;

            AppLog.Write($"TaskbarHost.Verify: visible={visible} rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) "
                       + $"size={w}x{h} insideTaskbar={insideTaskbar} topSibling={onTop}");
            LogSiblings();

            if (visible && sized && insideTaskbar)
            {
                LastStatus = "Embedded in the taskbar. Shell flyouts cannot cover the widget.";
                AppLog.Write("TaskbarHost: embed verified.");
                return;
            }

            // One retry. Explorer is still settling for a second or two after
            // logon, and being buried under a sibling is recoverable - give the
            // z-order a shove and re-check before giving up on the mode.
            if (_verifyAttempts++ < 1)
            {
                AppLog.Write("TaskbarHost: verification failed on the first pass - re-asserting layout and retrying.");
                ApplyLayout();
                EnsureTopAmongSiblings();
                _verifyTimer.Start();
                return;
            }

            LastStatus = $"Embedded but not visible (visible={visible}, size={w}x{h}, inside taskbar={insideTaskbar}). "
                       + "Reverted to the floating widget - see geetm.log.";
            AppLog.Write("TaskbarHost: embed could not be verified as visible - reverting to floating mode.");
            EmbedFailed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Write($"TaskbarHost.VerifyVisible failed: {ex.Message}");
            LastStatus = $"Could not verify the embed ({ex.Message}). Reverted to the floating widget.";
            EmbedFailed?.Invoke();
        }
    }

    /// <summary>Dumps the taskbar's direct children in z-order. On Windows 11
    /// the XAML host classes appearing above us is the single most likely
    /// explanation for an invisible embed, and this is how we find out.</summary>
    private void LogSiblings()
    {
        try
        {
            var sb = new System.Text.StringBuilder("TaskbarHost siblings (top first): ");
            var child = GetWindow(_trayWnd, GW_CHILD);
            int guard = 0;
            while (child != IntPtr.Zero && guard++ < 24)
            {
                var cls = new System.Text.StringBuilder(160);
                GetClassName(child, cls, cls.Capacity);
                GetWindowRect(child, out var cr);
                bool us = _source != null && child == _source.Handle;
                sb.Append($"[{(us ? "GeeTM->" : "")}{cls} {cr.Right - cr.Left}x{cr.Bottom - cr.Top}] ");
                child = GetWindow(child, GW_HWNDNEXT);
            }
            AppLog.Write(sb.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Write($"TaskbarHost.LogSiblings failed: {ex.Message}");
        }
    }

    /// <summary>Attempts the embed. Returns false (having logged why) rather
    /// than throwing, so the caller can fall back to floating mode cleanly.</summary>
    public bool TryEmbed(UIElement content)
    {
        _content = content;

        if (!ResolveTaskbarHandles())
        {
            LastStatus = "Could not find the taskbar window (Shell_TrayWnd). Using the floating widget.";
            AppLog.Write("TaskbarHost: could not find Shell_TrayWnd.");
            return false;
        }

        try
        {
            var parameters = new HwndSourceParameters("GeeTM.TaskbarHost")
            {
                ParentWindow = _trayWnd,
                WindowStyle = unchecked((int)(WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS)),
                ExtendedWindowStyle = unchecked((int)WS_EX_NOACTIVATE),
                PositionX = 0,
                PositionY = 0,
                Width = 120,
                Height = 32,
                UsesPerPixelOpacity = false // child windows cannot be layered per-pixel
            };

            _source = new HwndSource(parameters) { RootVisual = _content };

            if (_source.Handle == IntPtr.Zero)
            {
                LastStatus = "The child window could not be created. Using the floating widget.";
                AppLog.Write("TaskbarHost: HwndSource was created but has no handle.");
                Detach();
                return false;
            }

            // Some security software blocks child-window creation inside
            // Explorer. If that happened, GetParent comes back as something
            // other than the taskbar and the widget would render nowhere.
            if (GetParent(_source.Handle) != _trayWnd)
            {
                LastStatus = "The taskbar refused the child window - this is usually security software. Using the floating widget.";
                AppLog.Write("TaskbarHost: the child window did not attach to the taskbar (commonly blocked by security software).");
                Detach();
                return false;
            }

            AppLog.Write($"TaskbarHost: child hwnd=0x{_source.Handle.ToInt64():X} created under Shell_TrayWnd=0x{_trayWnd.ToInt64():X}.");
            LogSiblings();

            ApplyLayout();
            EnsureTopAmongSiblings();
            _layoutTimer.Start();
            InstallShellHooks();

            // Creating the window and getting the right parent back is NOT proof
            // that anything is on screen. On Windows 11 the taskbar's own XAML
            // surface can composite over a plain child HWND, which shows up as a
            // widget that is "embedded" and completely invisible. Verify shortly
            // after the fact and fall back if it never actually appears.
            _verifyTimer.Start();

            LastStatus = "Embedded - verifying it is actually visible...";
            AppLog.Write("TaskbarHost: embed created; awaiting visibility verification.");
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = $"Embedding failed: {ex.Message}. Using the floating widget.";
            AppLog.Write($"TaskbarHost.TryEmbed failed: {ex.Message}");
            Detach();
            return false;
        }
    }

    private bool ResolveTaskbarHandles()
    {
        _trayWnd = FindWindow("Shell_TrayWnd", null);
        if (_trayWnd == IntPtr.Zero) return false;

        // The notification area is only used as a positioning anchor. If the
        // shell version does not expose it under this class name, embed anyway
        // and dock against the taskbar's right edge instead of refusing.
        _notifyWnd = FindWindowEx(_trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
        return true;
    }

    /// <summary>Re-measures and repositions; re-embeds if Explorer replaced the
    /// taskbar underneath us. Called on a timer and on TaskbarCreated.</summary>
    public void Reconcile()
    {
        if (_disposed || _source == null) return;

        try
        {
            // A destroyed parent means Explorer restarted. Our child window went
            // with it, so the whole HwndSource has to be rebuilt.
            if (!IsWindow(_trayWnd) || GetParent(_source.Handle) != _trayWnd)
            {
                AppLog.Write("TaskbarHost: taskbar was recreated - re-embedding.");
                var content = _content;
                Detach();
                if (content != null && TryEmbed(content)) return;
                EmbedFailed?.Invoke();
                return;
            }

            ApplyLayout();
            EnsureTopAmongSiblings();
        }
        catch (Exception ex)
        {
            AppLog.Write($"TaskbarHost.Reconcile failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sizes the child to its content and parks it just left of the notification
    /// area. Everything here is in the taskbar's CLIENT coordinates, because a
    /// child window is positioned relative to its parent - mixing in screen
    /// coordinates is what puts the widget in the wrong place on secondary
    /// monitors and non-default taskbar layouts.
    /// </summary>
    private void ApplyLayout()
    {
        if (_source == null || _content == null) return;
        if (!IsWindow(_notifyWnd)) _notifyWnd = FindWindowEx(_trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);

        double dpi = _source.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (dpi <= 0) dpi = 1.0;

        // Size the child explicitly from the content's desired size. Relying on
        // HwndSource.SizeToContent left the window at its 1x1 creation size in
        // some cases, which is indistinguishable from "embedded but invisible".
        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _content.DesiredSize;
        int w = (int)Math.Ceiling(desired.Width * dpi);
        int h = (int)Math.Ceiling(desired.Height * dpi);

        if (w <= 1 || h <= 1)
        {
            AppLog.Write($"TaskbarHost.ApplyLayout: content measured {desired.Width}x{desired.Height} DIP - nothing to show yet.");
            return;
        }

        if (!GetWindowRect(_source.Handle, out var self)) return;

        // Anchor: the left edge of the notification area when we can find it,
        // otherwise the right edge of the taskbar itself.
        RECT anchorScreen;
        if (_notifyWnd != IntPtr.Zero && GetWindowRect(_notifyWnd, out var notifyRect))
        {
            anchorScreen = notifyRect;
        }
        else
        {
            if (!GetWindowRect(_trayWnd, out var trayRect)) return;
            anchorScreen = new RECT { Left = trayRect.Right, Top = trayRect.Top, Right = trayRect.Right, Bottom = trayRect.Bottom };
        }

        // Screen -> taskbar client coordinates.
        var anchorClient = new POINT { X = anchorScreen.Left, Y = anchorScreen.Top };
        if (!ScreenToClient(_trayWnd, ref anchorClient)) return;

        var s = SettingsService.Current;
        int anchorHeight = anchorScreen.Bottom - anchorScreen.Top;

        int x = anchorClient.X - w - (int)Math.Round(6 * dpi) + (int)Math.Round(s.WidgetOffsetX * dpi);
        int y = anchorClient.Y + ((anchorHeight - h) / 2) - (int)Math.Round(s.WidgetOffsetY * dpi);

        if (x < 0) x = 0;

        // Only touch the window when something actually changed - a redundant
        // SetWindowPos on a child of Explorer forces a taskbar repaint.
        var curClient = new POINT { X = self.Left, Y = self.Top };
        ScreenToClient(_trayWnd, ref curClient);
        int curW = self.Right - self.Left, curH = self.Bottom - self.Top;
        if (curClient.X == x && curClient.Y == y && curW == w && curH == h) return;

        SetWindowPos(_source.Handle, IntPtr.Zero, x, y, w, h,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        if (!_loggedFirstLayout)
        {
            _loggedFirstLayout = true;
            AppLog.Write($"TaskbarHost.ApplyLayout: dpi={dpi:0.##} placed at client ({x},{y}) size {w}x{h}; "
                       + $"anchor screen L={anchorScreen.Left} T={anchorScreen.Top} R={anchorScreen.Right} B={anchorScreen.Bottom}.");
        }
    }

    /// <summary>
    /// Keeps the widget at the top of the taskbar's own child z-order. This is
    /// a completely different thing from the topmost band that caused the
    /// original bug - it only orders us against Explorer's own children, so the
    /// clock or an icon repaint cannot end up drawn over us. Checked first so
    /// the common case costs one cheap call and never forces a repaint.
    /// </summary>
    private void EnsureTopAmongSiblings()
    {
        if (_source == null) return;
        var first = GetWindow(_trayWnd, GW_CHILD);
        if (first == _source.Handle) return;

        SetWindowPos(_source.Handle, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        AppLog.Write("TaskbarHost: re-asserted top-of-siblings after a shell event.");
    }

    /// <summary>Detaches the content from the child HWND without destroying the
    /// content itself, so the caller can hand the very same visual to the
    /// floating window as a fallback.</summary>
    private void Detach()
    {
        RemoveShellHooks();
        _layoutTimer.Stop();
        _verifyTimer.Stop();
        _loggedFirstLayout = false;
        _verifyAttempts = 0;
        try
        {
            if (_source != null)
            {
                _source.RootVisual = null; // release the content before disposing the source
                _source.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"TaskbarHost.Detach failed: {ex.Message}");
        }
        _source = null;
    }

    /// <summary>Releases the hosted content back to the caller and tears the
    /// child window down. Returns the content so it can be re-parented.</summary>
    public UIElement? Release()
    {
        var content = _content;
        Detach();
        _content = null;
        return content;
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
        _content = null;
    }

    // ---- Win32 ------------------------------------------------------------

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint GW_CHILD = 5;
    private const uint GW_HWNDNEXT = 2;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}



