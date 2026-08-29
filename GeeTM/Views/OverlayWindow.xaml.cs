using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GeeTM.Models;
using GeeTM.Services;

namespace GeeTM.Views;

/// <summary>
/// A small, click-through, always-on-top HUD shown while something else is
/// fullscreen (a game, a call, a fullscreen video) and overlay mode is
/// enabled - instead of the normal widget just hiding entirely. Positioned
/// in a screen corner; never steals focus or intercepts clicks.
/// </summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyOverlayStyles();
    }

    public void UpdateSpeed(NetSample sample)
    {
        try
        {
            var (upVal, upUnit) = UnitFormatter.Speed(sample.UploadBytesPerSec);
            var (downVal, downUnit) = UnitFormatter.Speed(sample.DownloadBytesPerSec);
            UpText.Text = $"{upVal} {upUnit}";
            DownText.Text = $"{downVal} {downUnit}";
        }
        catch (Exception ex)
        {
            AppLog.Write($"OverlayWindow.UpdateSpeed failed: {ex.Message}");
        }
    }

    /// <summary>Positions the overlay in the bottom-right corner of the
    /// primary screen's working area, with a small margin.</summary>
    public void PositionBottomRight()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Bottom - Height - 24;
        }
        catch (Exception ex)
        {
            AppLog.Write($"OverlayWindow.PositionBottomRight failed: {ex.Message}");
        }
    }

    // Same proven flags already used for the main widget's click-through and
    // no-activate behaviour elsewhere in this project - reused here rather
    // than re-derived, since they're already known to work correctly.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void ApplyOverlayStyles()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE,
                style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        }
        catch (Exception ex)
        {
            AppLog.Write($"OverlayWindow.ApplyOverlayStyles failed: {ex.Message}");
        }
    }
}
