using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using GeeTM.Models;
using GeeTM.Services;
// This project enables both UseWPF and UseWindowsForms, so implicit usings pull
// in System.Drawing and System.Windows.Forms alongside WPF. That makes a long
// list of type names ambiguous (Color, Brush, FontFamily, Point, Application,
// MessageBox, UserControl and more). Alias directives take precedence over
// namespace imports, so pinning the WPF meaning once here keeps the rest of
// the file readable instead of fully qualifying every use.
using UserControl = System.Windows.Controls.UserControl;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
// THE build fix: LinearGradientBrush takes System.Windows.Point, but with
// UseWindowsForms enabled a bare "Point" is ambiguous with System.Drawing.Point.
using Point = System.Windows.Point;

namespace GeeTM.Views;

/// <summary>
/// The widget's visual content, independent of how it is hosted. The floating
/// window and the taskbar-embedded native renderer both follow this same
/// layout contract, which is what keeps the two display modes from drifting
/// apart.
/// </summary>
public partial class WidgetSurface : UserControl
{
    /// <summary>Raised when the surface resizes, so whichever host owns it can
    /// re-apply its own positioning anchor.</summary>
    public event Action? SurfaceResized;

    private bool _isEmbedded;
    private Color? _lastAppliedBg;
    private double _lastAppliedOpacity = -1;

    public WidgetSurface()
    {
        InitializeComponent();
    }

    /// <summary>Embedded mode changes two things: the surface must paint an
    /// opaque background (a child window has no per-pixel transparency to fall
    /// back on), and the drop shadow is dropped because there is nothing outside
    /// the window's own bounds for it to fall on.</summary>
    public void SetEmbedded(bool embedded)
    {
        _isEmbedded = embedded;
        _lastAppliedBg = null; // force the next colour pass to actually reassign
        _lastAppliedOpacity = -1;
    }

    // ---- Appearance --------------------------------------------------------

    /// <summary>Full appearance pass: sizes, spacing, ordering, colours, fonts.
    /// Called at startup and after Settings is saved - never on a timer.</summary>
    public void ApplyAppearance()
    {
        var s = SettingsService.Current;
        double r = Math.Clamp(s.WidgetCornerRadius, 0, 20);
        bool onePod = s.PillShapeStyle == "OnePod";
        // Which pod sits on the left in the current ordering - needed so the
        // "outer corners rounded, inner corners square" treatment rounds the
        // correct sides regardless of which pod TotalBeforeSpeed puts first.
        bool speedIsLeft = !s.TotalBeforeSpeed;

        CornerRadius LeftPodRadius() => onePod ? new CornerRadius(r, 0, 0, r) : new CornerRadius(r);
        CornerRadius RightPodRadius() => onePod ? new CornerRadius(0, r, r, 0) : new CornerRadius(r);

        SpeedBorder.CornerRadius = speedIsLeft ? LeftPodRadius() : RightPodRadius();
        TotalBorder.CornerRadius = speedIsLeft ? RightPodRadius() : LeftPodRadius();
        SpeedBorder.Width = Math.Clamp(s.WidgetWidth, 70, 400);
        SpeedBorder.Height = Math.Clamp(s.WidgetHeight, 18, 120);
        TotalBorder.Height = SpeedBorder.Height;
        TotalBorder.MinWidth = 40;
        SpeedBorder.Padding = new Thickness(s.WidgetPaddingH, s.WidgetPaddingV, s.WidgetPaddingH, s.WidgetPaddingV);
        TotalBorder.Padding = new Thickness(s.TodayPaddingH, s.TodayPaddingV, s.TodayPaddingH, s.TodayPaddingV);

        if (s.PillBorderEnabled)
        {
            Brush borderBrush;
            try { borderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.PillBorderColorHex)!); }
            catch { borderBrush = Brushes.White; }
            double t = Math.Max(0.5, s.PillBorderThickness);

            // In "one pod" mode, the inner (facing) side of each pod gets a
            // zero-thickness border - that's what makes the two pods read as
            // a single shape divided by the gap, rather than two separately
            // outlined pods sitting next to each other.
            Thickness LeftPodBorder() => onePod ? new Thickness(t, t, 0, t) : new Thickness(t);
            Thickness RightPodBorder() => onePod ? new Thickness(0, t, t, t) : new Thickness(t);

            SpeedBorder.BorderBrush = borderBrush;
            SpeedBorder.BorderThickness = speedIsLeft ? LeftPodBorder() : RightPodBorder();
            TotalBorder.BorderBrush = borderBrush;
            TotalBorder.BorderThickness = speedIsLeft ? RightPodBorder() : LeftPodBorder();
        }
        else
        {
            SpeedBorder.BorderThickness = new Thickness(0);
            TotalBorder.BorderThickness = new Thickness(0);
        }
        UpGlyph.Margin = new Thickness(0, 0, s.WidgetIconTextGap, s.WidgetRowGap);
        DownGlyph.Margin = new Thickness(0, 0, s.WidgetIconTextGap, 0);
        TodayLabel.Margin = new Thickness(0, 0, 0, 1);
        GroupGapSpacer.Width = s.WidgetGroupGap;
        UpValueText.Margin = new Thickness(0, 0, 0, s.WidgetRowGap);
        UpUnitText.Margin = new Thickness(s.WidgetDigitUnitGap, 0, 0, s.WidgetRowGap);
        DownValueText.Margin = new Thickness(0);
        DownUnitText.Margin = new Thickness(s.WidgetDigitUnitGap, 0, 0, 0);
        TodayValueText.Margin = new Thickness(0);
        TodayUnitText.Margin = new Thickness(s.TodayDigitUnitGap, 0, 0, 0);

        // Individually hideable rows. Collapsing a row's grid row height as well
        // as its content stops a hidden row from leaving a blank band behind.
        bool showUp = s.ShowUploadRow || !s.ShowDownloadRow;   // never hide both
        bool showDown = s.ShowDownloadRow || !s.ShowUploadRow;
        UpGlyph.Visibility = showUp ? Visibility.Visible : Visibility.Collapsed;
        UpValuePanel.Visibility = showUp ? Visibility.Visible : Visibility.Collapsed;
        DownGlyph.Visibility = showDown ? Visibility.Visible : Visibility.Collapsed;
        DownValuePanel.Visibility = showDown ? Visibility.Visible : Visibility.Collapsed;
        UpRow.Height = showUp ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DownRow.Height = showDown ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        bool showTotal = s.ShowTodayInWidget;
        TotalBorder.Visibility = showTotal ? Visibility.Visible : Visibility.Collapsed;
        GroupGapSpacer.Visibility = showTotal ? Visibility.Visible : Visibility.Collapsed;

        // Only rebuild the children when the order actually differs. Clearing
        // and re-adding unconditionally detaches and reattaches live visuals,
        // which shows as a flash.
        bool totalIsFirst = ContainerStack.Children.Count > 0 && ReferenceEquals(ContainerStack.Children[0], TotalBorder);
        if (totalIsFirst != s.TotalBeforeSpeed || ContainerStack.Children.Count != 3)
        {
            ContainerStack.Children.Clear();
            if (s.TotalBeforeSpeed)
            {
                ContainerStack.Children.Add(TotalBorder);
                ContainerStack.Children.Add(GroupGapSpacer);
                ContainerStack.Children.Add(SpeedBorder);
            }
            else
            {
                ContainerStack.Children.Add(SpeedBorder);
                ContainerStack.Children.Add(GroupGapSpacer);
                ContainerStack.Children.Add(TotalBorder);
            }
        }
        ApplyShadow();
        ApplyColors();
        ApplyFonts();
    }

    private void ApplyShadow()
    {
        bool wantShadow = SettingsService.Current.WidgetShadow && !_isEmbedded;
        if (!wantShadow)
        {
            SpeedBorder.Effect = null;
            TotalBorder.Effect = null;
            return;
        }
        // One shared, frozen effect instance rather than two live ones: an
        // unfrozen Effect keeps a change-notification subscription per element
        // and is re-evaluated on every render pass.
        var shadow = new DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.32,
            Color = Colors.Black,
            RenderingBias = RenderingBias.Performance
        };
        shadow.Freeze();
        SpeedBorder.Effect = shadow;
        TotalBorder.Effect = shadow;
    }

    /// <summary>Background pass. Premium polish: the pod background is a single
    /// vertical LinearGradientBrush that carries the top hairline highlight,
    /// the solid pod colour, and the inner bottom shade in one draw. The cache
    /// check skips the assignment entirely when neither colour nor opacity
    /// changed, so the periodic auto-colour poll stays a no-op in the
    /// overwhelmingly common case.</summary>
    public void ApplyColors()
    {
        var s = SettingsService.Current;
        try
        {
            Color bg = s.ColorMode switch
            {
                WidgetColorMode.AutoExact => TaskbarColorService.GetTaskbarColor(),
                WidgetColorMode.AutoDarker => TaskbarColorService.Darken(TaskbarColorService.GetTaskbarColor(), s.AutoDarkenAmount),
                _ => (Color)ColorConverter.ConvertFromString(s.WidgetBackgroundHex)!
            };
            double opacity = _isEmbedded ? 1.0 : s.WidgetOpacity;

            if (_lastAppliedBg == bg && Math.Abs(_lastAppliedOpacity - opacity) < 0.001
                && SpeedBorder.Background is LinearGradientBrush)
            {
                return;
            }

            byte alpha = (byte)Math.Round(255 * opacity);
            Color baseColor = Color.FromArgb(alpha, bg.R, bg.G, bg.B);
            Color highlight = Color.FromArgb((byte)Math.Min(255, alpha * 0.25), 255, 255, 255); // 25% white hairline
            Color shade = Color.FromArgb((byte)Math.Min(255, alpha * 0.40), 0, 0, 0);           // 40% black inner shade

            var brush = new LinearGradientBrush(
                new GradientStopCollection(new[]
                {
                    new GradientStop(highlight, 0.0),
                    new GradientStop(baseColor, 0.05),
                    new GradientStop(baseColor, 0.95),
                    new GradientStop(shade, 1.0)
                }),
                new Point(0, 0), new Point(0, 1));
            brush.Freeze(); // frozen brushes render faster and are safe to share

            SpeedBorder.Background = brush;
            TotalBorder.Background = brush;

            // Embedded mode paints the whole child window, so any gap between
            // the two boxes has to be filled with the taskbar's own colour
            // rather than showing through as black.
            if (_isEmbedded)
            {
                var taskbar = new SolidColorBrush(TaskbarColorService.GetTaskbarColor());
                taskbar.Freeze();
                SurfaceRoot.Background = taskbar;
                GroupGapSpacer.Background = taskbar;
            }
            else
            {
                SurfaceRoot.Background = Brushes.Transparent;
                GroupGapSpacer.Background = Brushes.Transparent;
            }
            _lastAppliedBg = bg;
            _lastAppliedOpacity = opacity;
        }
        catch (Exception ex)
        {
            AppLog.Write($"WidgetSurface.ApplyColors failed, using theme default: {ex.Message}");
            var fallback = (Brush)FindResource("BgBrush");
            SpeedBorder.Background = fallback;
            TotalBorder.Background = fallback;
        }
    }

    private void ApplyFonts()
    {
        var s = SettingsService.Current;
        var fontFamily = new FontFamily(s.WidgetFontFamily);
        var textBrush = (Brush)FindResource("TextPrimaryBrush");
        var digitWeight = s.WidgetDigitsBold ? FontWeights.SemiBold : FontWeights.Normal;
        var unitWeight = s.WidgetUnitBold ? FontWeights.SemiBold : FontWeights.Normal;
        foreach (var tb in new[] { UpValueText, DownValueText })
        {
            tb.FontFamily = fontFamily; tb.FontSize = s.WidgetFontSize;
            tb.FontWeight = digitWeight; tb.Foreground = textBrush;
        }
        foreach (var tb in new[] { UpUnitText, DownUnitText })
        {
            tb.FontFamily = fontFamily; tb.FontSize = s.WidgetFontSize;
            tb.FontWeight = unitWeight; tb.Foreground = textBrush;
        }
        UpGlyph.FontSize = s.WidgetFontSize;
        DownGlyph.FontSize = s.WidgetFontSize;
        UpGlyph.FontFamily = fontFamily;
        DownGlyph.FontFamily = fontFamily;
        TodayValueText.FontFamily = fontFamily;
        TodayValueText.FontWeight = s.TodayDigitsBold ? FontWeights.SemiBold : FontWeights.Normal;
        TodayValueText.FontSize = s.TodayFontSize;
        TodayValueText.Foreground = textBrush;
        TodayUnitText.FontFamily = fontFamily;
        TodayUnitText.FontWeight = s.TodayUnitBold ? FontWeights.SemiBold : FontWeights.Normal;
        TodayUnitText.FontSize = s.TodayFontSize;
        TodayUnitText.Foreground = textBrush;

        // Premium polish: uppercase micro-label, matching the embedded renderer.
        TodayLabel.Text = string.IsNullOrEmpty(s.TodayLabelText) ? "" : s.TodayLabelText.ToUpperInvariant();
        TodayLabel.FontFamily = fontFamily;
        TodayLabel.FontSize = s.TodayFontSize;
        TodayLabel.Foreground = (Brush)FindResource("TextMutedBrush");

        // Speed pod's rotated-content text (IP/location) - styled identically
        // to Today's, since it uses the exact same visual layout.
        SpeedRotValueText.FontFamily = fontFamily;
        SpeedRotValueText.FontWeight = s.TodayDigitsBold ? FontWeights.SemiBold : FontWeights.Normal;
        SpeedRotValueText.FontSize = s.TodayFontSize;
        SpeedRotValueText.Foreground = textBrush;
        SpeedRotLabelText.FontFamily = fontFamily;
        SpeedRotLabelText.FontSize = s.TodayFontSize;
        SpeedRotLabelText.Foreground = (Brush)FindResource("TextMutedBrush");
    }

    // ---- Live data ---------------------------------------------------------

    /// <summary>Pushes one poll tick into the display. All formatting goes
    /// through UnitFormatter so binary/decimal units and bits-per-second mode
    /// are honoured consistently everywhere.</summary>
    public void Update(NetSample sample, UsageTotals totals)
    {
        var (downVal, downUnit) = UnitFormatter.Speed(sample.DownloadBytesPerSec);
        var (upVal, upUnit) = UnitFormatter.Speed(sample.UploadBytesPerSec);
        DownValueText.Text = downVal; DownUnitText.Text = downUnit;
        UpValueText.Text = upVal; UpUnitText.Text = upUnit;
        long counted = SettingsService.Current.TodayShowsMonth
            ? totals.BytesReceivedMonth + totals.BytesSentMonth
            : totals.BytesReceivedToday + totals.BytesSentToday;
        var (totalVal, totalUnit) = UnitFormatter.Total(counted);
        TodayValueText.Text = totalVal;
        TodayUnitText.Text = totalUnit;

        ApplyRotatingPillContent();
    }

    // v5.0 rotating pill: overrides the Today pill's TextBlocks with IP or
    // country-code content when it's their turn in the rotation. WPF's own
    // layout system re-measures the pod automatically whenever these .Text
    // values change, so no manual width calculation is needed here (unlike
    // the embedded widget's native GDI+ path, which has to measure text
    // itself) - this is genuinely the simpler half of this feature.
    public void ApplyRotatingPillContent()
    {
        try
        {
            var s = SettingsService.Current;
            bool todayJustChanged = RotatingPillHelper.JustChanged(s, "Today");
            bool speedJustChanged = RotatingPillHelper.JustChanged(s, "Speed");

            var todayState = RotatingPillHelper.GetCurrent(s, "Today");
            if (todayState != RotatingPillHelper.PillContent.Base)
            {
                var info = PublicIpService.GetCached();
                if (TryResolveContent(todayState, info, out var label, out var value))
                {
                    TodayLabel.Text = label;
                    TodayValueText.Text = value;
                    TodayUnitText.Text = "";
                    if (todayJustChanged) FadeIn(TotalBorder);
                }
                // else: not resolved yet - normal Today content stays as already set
            }
            else if (todayJustChanged)
            {
                FadeIn(TotalBorder); // fading back to normal Today content too, for the same smoothness on the way back
            }

            var speedState = RotatingPillHelper.GetCurrent(s, "Speed");
            if (speedState != RotatingPillHelper.PillContent.Base)
            {
                var info = PublicIpService.GetCached();
                if (TryResolveContent(speedState, info, out var label, out var value))
                {
                    SpeedRotLabelText.Text = label;
                    SpeedRotValueText.Text = value;
                    SpeedRowsGrid.Visibility = Visibility.Collapsed;
                    SpeedRotatedPanel.Visibility = Visibility.Visible;
                    if (speedJustChanged) FadeIn(SpeedBorder);
                    return;
                }
            }

            // Speed pod not currently rotated (or content not resolved yet) -
            // make sure it's showing its normal two-row layout.
            bool wasRotated = SpeedRotatedPanel.Visibility == Visibility.Visible;
            SpeedRowsGrid.Visibility = Visibility.Visible;
            SpeedRotatedPanel.Visibility = Visibility.Collapsed;
            if (wasRotated) FadeIn(SpeedBorder); // just switched back to normal - fade that transition too
        }
        catch (Exception ex)
        {
            AppLog.Write($"WidgetSurface.ApplyRotatingPillContent failed: {ex.Message}");
            // Fail safe to the normal, always-correct two-row layout.
            SpeedRowsGrid.Visibility = Visibility.Visible;
            SpeedRotatedPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Quick opacity crossfade - the pod's size may jump slightly
    /// between content types (it resizes to fit whatever it's showing), and
    /// fading rather than hard-cutting softens that resize instead of it
    /// reading as a rough pop. Kept intentionally simple: a single opacity
    /// animation is safe, standard WPF, nothing that risks visual glitches.</summary>
    private static void FadeIn(UIElement el)
    {
        var anim = new DoubleAnimation
        {
            From = 0.15,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private static bool TryResolveContent(RotatingPillHelper.PillContent state, PublicIpService.IpInfo info, out string label, out string value)
    {
        label = ""; value = "";
        if (state == RotatingPillHelper.PillContent.Ip)
        {
            if (string.IsNullOrEmpty(info.Ip)) return false;
            label = "IP:" + ThreatCheckService.GetLabelSuffix(info.Ip) + " ";
            value = info.Ip;
            return true;
        }
        if (state == RotatingPillHelper.PillContent.Flag)
        {
            if (string.IsNullOrEmpty(info.CountryCode)) return false;
            label = "LOCATION: ";
            // Real flag emoji here specifically - floating mode's text
            // rendering goes through WPF/DirectWrite, which correctly
            // handles the multi-codepoint regional-indicator sequences flags
            // are built from, plus color emoji in general. Embedded mode's
            // native GDI+ renderer does not reliably do either, which is why
            // that path (NativeEmbeddedWidget.TryResolveContent) still uses
            // the plain country code text instead.
            value = PublicIpService.CountryCodeToFlagEmoji(info.CountryCode) ?? info.CountryCode;
            return true;
        }
        return false;
    }

    private void ContainerStack_SizeChanged(object sender, SizeChangedEventArgs e) => SurfaceResized?.Invoke();
}



