using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GeeTM.Services;

/// <summary>
/// Single owner of the widget's z-order and repaint health.
///
/// This replaces the four independent recovery paths that used to fight each
/// other (mouse hook, foreground hook, Deactivated event, keep-on-top timer).
/// It fixes three concrete defects:
///
/// 1. THE MAIN BUG ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â SetWindowPos(hwnd, HWND_TOPMOST, ...) is a NO-OP for
///    z-order when the window already has WS_EX_TOPMOST set. Windows sees
///    "you're already topmost" and does not re-raise the window inside the
///    topmost band. Every single recovery path in the old code called exactly
///    that, so once Explorer pushed the widget below Shell_TrayWnd during a
///    shell flyout, NOTHING could bring it back ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the 500 ms timer ticked
///    forever doing nothing. Clicking another window worked only because that
///    made Windows re-sort the band by itself. The fix is a real band exit and
///    re-entry: HWND_NOTOPMOST then HWND_TOPMOST.
///
/// 2. Verification instead of guessing. Rather than firing blind reasserts on
///    every mouse click and foreground change, we walk the z-order and ask the
///    only question that matters: "is any visible, non-cloaked window that
///    overlaps our rectangle sitting above us?" We only act when the answer is
///    yes, which removes the flicker the constant reasserting was causing.
///
/// 3. Layered-window repaint. AllowsTransparency="True" makes this a
///    WS_EX_LAYERED window. Windows 11 routinely skips re-presenting layered
///    surfaces after a shell flyout animation, so even a correct z-order can
///    leave a blank area behind. After every reassert we force an actual
///    re-present.
///
/// It listens to the shell object events the Start menu / Quick Settings /
/// Notification Center actually raise (EVENT_OBJECT_SHOW / HIDE / CLOAKED /
/// UNCLOAKED), not just EVENT_SYSTEM_FOREGROUND ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â which Win11 XAML-island
/// flyouts frequently never raise at all.
/// </summary>
public sealed class OverlayGuardService : IDisposable
{
    private readonly Window _window;
    private IntPtr _hwnd;
    private bool _suspended;
    private bool _disposed;

    private readonly DispatcherTimer _verifyTimer;
    private readonly WinEventDelegate _callback;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private IntPtr _showHideHook = IntPtr.Zero;
    private IntPtr _cloakHook = IntPtr.Zero;

    private DateTime _lastReassert = DateTime.MinValue;
    private int _burstsPending;

    public OverlayGuardService(Window window)
    {
        _window = window;
        _callback = OnWinEvent; // must stay rooted for the hook's lifetime

        // 250 ms backstop. It costs almost nothing because the z-order walk
        // stops the moment it reaches our own window, and a topmost window is
        // near the front of the list. It only calls SetWindowPos when the
        // widget is genuinely buried.
        _verifyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _verifyTimer.Tick += (_, _) => VerifyAndHeal(force: false);
    }

    private bool _started;

    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _openShellSurfaces.Clear();
        _hiddenByShellFlyout = false;
        _hideFlyoutTimer?.Stop();
        _hideFlyoutTimer = null;
        _showFlyoutTimer?.Stop();
        _showFlyoutTimer = null;

        // Start() is re-entrant: switching display modes calls it again. Without
        // this guard each switch would install another set of WinEvent hooks
        // that never get unhooked.
        if (_started) { VerifyAndHeal(force: true); return; }
        _started = true;

        try
        {
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            // The important ones. Win11 shell surfaces are shown/hidden and
            // cloaked/uncloaked rather than cleanly taking foreground.
            // Deliberately two narrow ranges rather than one wide one: a single
            // 0x8001-0x8018 range would also capture EVENT_OBJECT_LOCATIONCHANGE,
            // which fires on every frame of every window move and resize.
            _showHideHook = SetWinEventHook(
                EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _cloakHook = SetWinEventHook(
                EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (_foregroundHook == IntPtr.Zero || _showHideHook == IntPtr.Zero || _cloakHook == IntPtr.Zero)
                AppLog.Write("OverlayGuard: a WinEvent hook failed to install ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â falling back to the 250 ms verifier only.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"OverlayGuard.Start hook install failed: {ex.Message}");
        }

        _verifyTimer.Start();
        VerifyAndHeal(force: true);
    }

    /// <summary>Used while genuine fullscreen content is active ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the widget
    /// stands down completely instead of clawing its way over a game.</summary>
    public void Suspend()
    {
        _suspended = true;
        _hideFlyoutTimer?.Stop();
        _hideFlyoutTimer = null;
        _showFlyoutTimer?.Stop();
        _showFlyoutTimer = null;
        _openShellSurfaces.Clear();
        _hiddenByShellFlyout = false;
    }

    public void Resume()
    {
        _suspended = false;
        _openShellSurfaces.Clear();
        _hiddenByShellFlyout = false;
        ScheduleBurst();
    }

    // ---- Shell event handling ---------------------------------------------

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // CycleActivation() deliberately forces two real foreground changes on
        // this exact window (onto it, then back off). Both of those fire
        // EVENT_SYSTEM_FOREGROUND with hwnd == _hwnd, which - before this
        // check existed - fed straight back into ScheduleBurst and triggered
        // another CycleActivation, forever. A device log showed this actually
        // happening: the same heal cycle repeating multiple times a second for
        // over 40 seconds straight. Any event whose hwnd is our own window is
        // an event we caused, not one to react to.
        if (hwnd == _hwnd) return;

        try
        {
            if (_disposed || _suspended || hwnd == _hwnd) return;

            // Only top-level window events matter. This single check discards
            // the overwhelming majority of EVENT_OBJECT_* traffic (caret,
            // focus, menu item, value-change noise) before we do anything
            // expensive, which is what keeps a broad hook range cheap.
            if (idObject != OBJID_WINDOW) return;

            string className = GetClassNameOf(hwnd);
            bool isShellClass = IsShellSurfaceClass(className);
            if (eventType != EVENT_SYSTEM_FOREGROUND && !isShellClass) return;

            AppLog.Write($"OverlayGuard: shell event 0x{eventType:X} class='{className}' hwnd=0x{hwnd.ToInt64():X} - scheduling a heal burst.");
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScheduleBurst));

            // Real device logs showed the 60fps fight from here still losing
            // against Windows 11's privileged shell compositing regardless of
            // how it fought, while ALSO being a visible flicker source in its
            // own right. This routes to a clean hide/restore instead - losing
            // quietly rather than losing loudly.
            bool isFlyoutClass = IsShellFlyoutClass(className);

            if (isFlyoutClass && eventType is EVENT_OBJECT_SHOW or EVENT_OBJECT_UNCLOAKED or EVENT_SYSTEM_FOREGROUND)
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnShellSurfaceOpened(hwnd)));
            }
            else if (isFlyoutClass && eventType is EVENT_OBJECT_HIDE or EVENT_OBJECT_DESTROY)
            {
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnShellSurfaceClosed(hwnd)));
            }
            else if (!isFlyoutClass && eventType == EVENT_SYSTEM_FOREGROUND)
            {
                // The device log showed the old hwnd-pair tracking never
                // stopping via a matching close event - only ever via a hard
                // safety ceiling. Windows 11's XAML-hosted flyouts frequently
                // don't fire a HIDE on the same handle that fired SHOW.
                // Foreground moving to an ORDINARY window is the same signal
                // "clicking another window" always was - use it as an
                // authoritative close too, not just the SHOW/HIDE pairing.
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnForegroundMovedAwayFromShell(hwnd)));
            }
        }
        catch
        {
            // A throwing WinEvent callback can destabilise the hook ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â never let one escape.
        }
    }

    /// <summary>
    /// True for the window classes Explorer / ShellExperienceHost use for the
    /// Start menu, Quick Settings, the volume, battery and network flyouts,
    /// Notification Center, and the taskbar itself.
    /// </summary>
    private static bool IsShellSurfaceClass(string cls) => cls is "Shell_TrayWnd"
        or "Shell_SecondaryTrayWnd"
        or "TrayNotifyWnd"
        or "NotifyIconOverflowWindow"
        or "TopLevelWindowForOverflowXamlIsland"
        or "Windows.UI.Core.CoreWindow"
        or "XamlExplorerHostIslandWindow"
        or "Xaml_WindowedPopupClass"
        or "ControlCenterWindow"
        or "Shell_InputSwitchTopLevelWindow"
        or "ForegroundStaging"
        or "MultitaskingViewFrame";

    /// <summary>Real flyouts/popups only - deliberately excludes the
    /// persistent taskbar classes (Shell_TrayWnd, TrayNotifyWnd, and
    /// friends), which are always present and would otherwise trigger a hide
    /// on every single heal burst rather than only while something is
    /// actually covering the widget.</summary>
    private static bool IsShellFlyoutClass(string cls) => cls is
        "Windows.UI.Core.CoreWindow"
        or "XamlExplorerHostIslandWindow"
        or "Xaml_WindowedPopupClass"
        or "ControlCenterWindow"
        or "Shell_InputSwitchTopLevelWindow"
        or "ForegroundStaging"
        or "MultitaskingViewFrame"
        or "TopLevelWindowForOverflowXamlIsland";

    // ---- Clean hide/restore around real shell flyouts ----------------------
    //
    // Real device logs proved two things conclusively: the 16ms/60fps contest
    // that used to run here while a flyout was open is a genuine, visible
    // flicker source, and it still loses the fight against Windows 11's
    // privileged shell compositing regardless. Fighting loudly and losing is
    // worse than losing quietly. This replaces the fight with a deliberate
    // hide while a real flyout is open, and a clean restore when it closes.

    private readonly HashSet<IntPtr> _openShellSurfaces = new();
    private DispatcherTimer? _hideFlyoutTimer;
    private DispatcherTimer? _showFlyoutTimer;
    private bool _hiddenByShellFlyout;

    // Windows 11 reuses the same window classes (Xaml_WindowedPopupClass,
    // XamlExplorerHostIslandWindow, and others) for BOTH a small hover
    // tooltip - the date/time preview, a taskbar icon preview - AND the
    // actual Start menu or Quick Settings panel. Real device logs proved
    // class name alone can't tell them apart: every ordinary hover was being
    // treated as a real flyout and hiding the widget for it, which is wrong.
    // Size can tell them apart reliably - a hover tooltip is small, Start and
    // Quick Settings are not. Chosen conservatively so a small jump list or
    // tooltip never crosses it; if a genuine flyout on some display config
    // turns out smaller than this, the logged size below is what to retune it
    // against.
    private const int MinFlyoutWidth = 300;
    private const int MinFlyoutHeight = 250;

    private void OnShellSurfaceOpened(IntPtr hwnd)
    {
        if (_disposed || _suspended) return;

        string cls = GetClassNameOf(hwnd);
        if (!IsShellFlyoutClass(cls)) return;

        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        bool bigEnough = w >= MinFlyoutWidth && h >= MinFlyoutHeight;

        if (!bigEnough)
        {
            AppLog.Write($"OverlayGuard: shell popup observed but too small to be a real flyout - class='{cls}' size={w}x{h} - not hiding.");
            return;
        }

        AppLog.Write($"OverlayGuard: shell flyout opened - class='{cls}', hwnd=0x{hwnd.ToInt64():X}, size={w}x{h}.");
        _openShellSurfaces.Add(hwnd);

        _showFlyoutTimer?.Stop();
        _showFlyoutTimer = null;

        if (_hiddenByShellFlyout) return;

        // Small delay rather than hiding instantly: some shell surfaces
        // appear briefly during hover/animation and never actually cover the
        // widget. If the surface is still tracked as open after the delay,
        // hide cleanly.
        if (_hideFlyoutTimer == null)
        {
            _hideFlyoutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _hideFlyoutTimer.Tick += (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                _hideFlyoutTimer = null;
                if (_disposed || _suspended) return;
                if (_openShellSurfaces.Count > 0) HideForShellFlyout();
            };
            _hideFlyoutTimer.Start();
        }
    }

    /// <summary>Foreground genuinely left every shell surface we were
    /// tracking. Treated the same as an explicit close, since it's the same
    /// real-world event as the person clicking another window.</summary>
    private void OnForegroundMovedAwayFromShell(IntPtr newForegroundHwnd)
    {
        if (_openShellSurfaces.Count == 0) return;
        AppLog.Write($"OverlayGuard: foreground moved away from tracked shell flyouts (new foreground=0x{newForegroundHwnd.ToInt64():X}) - treating them as closed.");
        _openShellSurfaces.Clear();
        ScheduleShowAfterShellClose();
    }

    private void OnShellSurfaceClosed(IntPtr hwnd)
    {
        _openShellSurfaces.Remove(hwnd);
        if (_openShellSurfaces.Count > 0) return; // another tracked surface is still open
        ScheduleShowAfterShellClose();
    }

    private void ScheduleShowAfterShellClose()
    {
        _hideFlyoutTimer?.Stop();
        _hideFlyoutTimer = null;

        if (!_hiddenByShellFlyout) return;
        if (_showFlyoutTimer != null) return;

        // Same reasoning as the hide delay: a flyout's close sequence often
        // hides several internal sub-windows in quick succession, so a short
        // grace period avoids restoring mid-animation only to hide again
        // a moment later.
        _showFlyoutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _showFlyoutTimer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            _showFlyoutTimer = null;
            if (_disposed || _suspended) return;
            if (_openShellSurfaces.Count == 0) ShowAfterShellFlyout();
        };
        _showFlyoutTimer.Start();
    }

    private void HideForShellFlyout()
    {
        if (_hiddenByShellFlyout) return;
        _hiddenByShellFlyout = true;
        if (_window.Visibility == Visibility.Visible)
        {
            _window.Visibility = Visibility.Hidden;
            AppLog.Write("OverlayGuard: hiding widget while shell flyout is open.");
        }
    }

    private void ShowAfterShellFlyout()
    {
        if (!_hiddenByShellFlyout) return;
        _hiddenByShellFlyout = false;
        if (!_suspended && _window.Visibility != Visibility.Visible)
        {
            _window.Visibility = Visibility.Visible;
            AppLog.Write("OverlayGuard: shell flyout closed - restoring widget.");
            VerifyAndHeal(force: true);
        }
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(160);
        return GetClassName(hwnd, sb, sb.Capacity) == 0 ? string.Empty : sb.ToString();
    }

    /// <summary>
    /// A shell transition is an animation, not an instant. Verify at several
    /// points across the ~600 ms it takes for a flyout to open or close so we
    /// heal both during and after it, without a permanently fast timer.
    /// </summary>
    private void ScheduleBurst()
    {
        if (_disposed || _suspended) return;
        if (_burstsPending > 3) return; // already covered by an in-flight burst

        foreach (var delay in new[] { 0, 80, 200, 420, 700 })
        {
            _burstsPending++;
            var t = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(delay)
            };
            t.Tick += (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                _burstsPending--;
                VerifyAndHeal(force: false);
            };
            t.Start();
        }
    }

    // ---- The actual check + heal ------------------------------------------

    /// <summary>
    /// Walks the z-order from the front and stops at the first thing it finds.
    /// If it reaches our own window first, we are on top and there is nothing
    /// to do ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â this is the normal case and it exits after only a handful of
    /// iterations. If it finds a visible, non-cloaked, overlapping window
    /// first, we are buried.
    /// </summary>
    private bool IsBuried()
    {
        if (!GetWindowRect(_hwnd, out var self)) return false;
        if (self.Right <= self.Left || self.Bottom <= self.Top) return false;

        int guard = 0;
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && guard < 400; h = GetWindow(h, GW_HWNDNEXT), guard++)
        {
            if (h == _hwnd) return false;              // we got here first ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â we're on top
            if (!IsWindowVisible(h)) continue;

            var sb = new StringBuilder(160);
            GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();

            // A device log showed the actual Start menu SHOW/HIDE events firing
            // correctly, five follow-up checks running across the flyout's
            // animation, and every single one finding nothing overlapping - while
            // the widget was genuinely hidden underneath it. The only window
            // that could have been responsible was being skipped here, because
            // DWM reports Windows 11's shell surfaces as "cloaked" as an
            // implementation detail of how they composite, not because they are
            // off-screen. Cloaking still means "skip it" for an ordinary
            // background window on another virtual desktop; it does NOT mean
            // that for the specific shell classes this app already knows to
            // recognise, so those are checked for overlap regardless of the
            // cloak bit.
            bool cloaked = IsCloaked(h);
            bool isShell = IsShellSurfaceClass(cls);
            if (cloaked && !isShell)
            {
                // Kept cheap: only worth logging when it would otherwise have
                // been counted as burial, so an unrecognised occluding window
                // class shows up here if this fix turns out to be incomplete.
                if (GetWindowRect(h, out var skippedRect)
                    && skippedRect.Left < self.Right && skippedRect.Right > self.Left
                    && skippedRect.Top < self.Bottom && skippedRect.Bottom > self.Top)
                {
                    AppLog.Write($"OverlayGuard.IsBuried: skipped cloaked, overlapping, non-shell window '{cls}' - "
                               + "if the widget is still hidden after this build, this class is a candidate to add.");
                }
                continue;
            }

            if (!GetWindowRect(h, out var r)) continue;
            if (r.Right <= r.Left || r.Bottom <= r.Top) continue;

            bool overlaps = r.Left < self.Right && r.Right > self.Left
                         && r.Top < self.Bottom && r.Bottom > self.Top;
            if (overlaps)
            {
                AppLog.Write($"OverlayGuard.IsBuried: overlapped by '{cls}' (cloaked={cloaked}, shellSurface={isShell}) "
                           + $"rect=({r.Left},{r.Top},{r.Right},{r.Bottom}).");
                return true;
            }
        }
        return false;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    public void VerifyAndHeal(bool force)
    {
        if (_disposed || _suspended || _hwnd == IntPtr.Zero) return;
        if (_hiddenByShellFlyout) return; // intentionally hidden - nothing to heal until it's restored

        try
        {
            bool buried = IsBuried();
            if (!force && !buried) return;

            // Rate-limit so overlapping bursts can't machine-gun SetWindowPos.
            // Widened from 40ms: the hide/restore path above now handles the
            // case that used to need rapid re-fighting, so there is no longer
            // a reason for this to run as tightly.
            if ((DateTime.UtcNow - _lastReassert).TotalMilliseconds < 250) return;
            _lastReassert = DateTime.UtcNow;

            AppLog.Write($"OverlayGuard.VerifyAndHeal: buried={buried} force={force} - reasserting and repainting.");
            Reassert();
            ForceRepaint();

            bool stillBuried = IsBuried();
            AppLog.Write($"OverlayGuard.VerifyAndHeal: post-heal check buried={stillBuried}.");

            // NudgePosition() (a real 1px geometry move) is deliberately no
            // longer called here. It was a legitimate fix for a stale
            // composited surface, but real device logs showed it firing on
            // every single heal and being a visible flicker source in its own
            // right. The hide/restore path above now covers the case it was
            // compensating for - the widget is hidden while genuinely covered
            // rather than left showing a stale frame.
        }
        catch (Exception ex)
        {
            AppLog.Write($"OverlayGuard.VerifyAndHeal failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The real fix. Dropping to HWND_NOTOPMOST first clears WS_EX_TOPMOST, so
    /// the following HWND_TOPMOST call is a genuine band entry and lands the
    /// window at the FRONT of the topmost band. Calling HWND_TOPMOST alone on
    /// an already-topmost window changes nothing at all ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â which is precisely
    /// why the widget used to stay buried until you clicked elsewhere.
    /// SWP_NOACTIVATE keeps focus where it is; SWP_NOOWNERZORDER stops the
    /// owner chain being dragged along.
    /// </summary>
    private void Reassert()
    {
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING;
        SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>
    /// A WS_EX_LAYERED WPF window frequently isn't re-presented by DWM after a
    /// shell animation, so the pixels stay stale (or blank) even once the
    /// z-order is right. RedrawWindow handles the native side; InvalidateVisual
    /// asks WPF to actually re-render its own visual tree.
    /// </summary>
    private void ForceRepaint()
    {
        try
        {
            RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME);

            if (_window.Content is UIElement root)
            {
                root.InvalidateVisual();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"OverlayGuard.ForceRepaint failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _started = false;
        _verifyTimer.Stop();
        _hideFlyoutTimer?.Stop();
        _showFlyoutTimer?.Stop();
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_showHideHook != IntPtr.Zero) { UnhookWinEvent(_showHideHook); _showHideHook = IntPtr.Zero; }
        if (_cloakHook != IntPtr.Zero) { UnhookWinEvent(_cloakHook); _cloakHook = IntPtr.Zero; }
    }

    // ---- Win32 ------------------------------------------------------------

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;     // range 0x8001-0x8003 covers DESTROY, SHOW, HIDE
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;

    private const uint GW_HWNDNEXT = 2;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}



