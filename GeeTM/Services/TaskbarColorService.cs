using System.Runtime.InteropServices;
using WpfColor = System.Windows.Media.Color;

namespace GeeTM.Services;

public enum WidgetColorMode { Custom, AutoExact, AutoDarker }

public static class TaskbarColorService
{
    private static WpfColor? _lastGood;

    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("dwmapi.dll")] private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint CLR_INVALID = 0xFFFFFFFF;

    public static WpfColor GetTaskbarColor(int? sampleXOverride = null, int? sampleYOverride = null)
    {
        try
        {
            IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
            if (trayWnd != IntPtr.Zero && GetWindowRect(trayWnd, out var rect))
            {
                int sampleX = sampleXOverride ?? (rect.Left + 6);
                int sampleY = sampleYOverride ?? ((rect.Top + rect.Bottom) / 2);
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    try
                    {
                        uint px = GetPixel(dc, sampleX, sampleY);
                        if (px != CLR_INVALID)
                        {
                            var c = WpfColor.FromRgb(
                                (byte)(px & 0xFF),
                                (byte)((px >> 8) & 0xFF),
                                (byte)((px >> 16) & 0xFF));
                            c = StabiliseBrightness(c);
                            _lastGood = c;
                            return c;
                        }
                    }
                    finally { ReleaseDC(IntPtr.Zero, dc); }
                }
            }
        }
        catch (Exception ex) { AppLog.Write($"TaskbarColorService.GetTaskbarColor failed: {ex.Message}"); }
        
        if (_lastGood.HasValue) return _lastGood.Value;

        try
        {
            if (DwmGetColorizationColor(out uint colorization, out _) == 0)
            {
                return WpfColor.FromRgb(
                    (byte)((colorization >> 16) & 0xFF),
                    (byte)((colorization >> 8) & 0xFF),
                    (byte)(colorization & 0xFF));
            }
        }
        catch { }
        return WpfColor.FromRgb(0x1F, 0x1F, 0x1F);
    }

    private static WpfColor StabiliseBrightness(WpfColor c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        int chroma = max - min;
        const int MinChroma = 25;
        if (chroma < MinChroma || max == 0) return c;
        const double Target = 200.0;
        if (max >= Target) return c;
        double scale = Target / max;
        scale = Math.Min(scale, 2.2);
        return WpfColor.FromRgb(
            (byte)Math.Min(255, Math.Round(c.R * scale)),
            (byte)Math.Min(255, Math.Round(c.G * scale)),
            (byte)Math.Min(255, Math.Round(c.B * scale)));
    }

    public static WpfColor Darken(WpfColor c, double amount = 0.35)
    {
        byte R(byte v) => (byte)Math.Clamp(v * (1 - amount), 0, 255);
        return WpfColor.FromRgb(R(c.R), R(c.G), R(c.B));
    }
}



