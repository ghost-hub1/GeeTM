using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// Fix for CS0104: UseWindowsForms and UseWPF both define a ComboBox.
using ComboBox = System.Windows.Controls.ComboBox;

namespace GeeTM.Services;

public static class SmoothScroll
{
    private const double BaseStepPixels = 34;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    private static readonly HashSet<Window> _hookedWindows = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.CanContentScroll = false;

            var win = Window.GetWindow(sv);
            if (win != null && _hookedWindows.Add(win))
            {
                win.PreviewMouseWheel += Window_PreviewMouseWheel;
                win.Closed += (_, _) =>
                {
                    win.PreviewMouseWheel -= Window_PreviewMouseWheel;
                    _hookedWindows.Remove(win);
                };
            }
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not Window win) return;

        var combo = FindOpenComboBox(win);
        if (combo == null) return;

        var ddSv = FindDropDownScrollViewer(combo);
        if (ddSv != null)
        {
            double speed = SettingsService.Current.ScrollSpeed;
            if (speed <= 0 || double.IsNaN(speed)) speed = 1.0;
            speed = Math.Clamp(speed, 0.2, 3.0);

            double delta = -(e.Delta / 120.0) * BaseStepPixels * speed;
            double target = Math.Clamp(
                ddSv.VerticalOffset + delta,
                0,
                Math.Max(0, ddSv.ExtentHeight - ddSv.ViewportHeight));
            ddSv.ScrollToVerticalOffset(target);
        }

        e.Handled = true;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.ScrollableHeight <= 0) return;

        // No dropdown-open check needed here: Window_PreviewMouseWheel (which
        // searches the WHOLE window) always runs first, since PreviewMouseWheel
        // tunnels from the Window down to this ScrollViewer. If it found an
        // open dropdown, it already scrolled it and set e.Handled = true,
        // which the guard above already caught. If it found nothing, there is
        // nothing in this ScrollViewer's smaller subtree either.
        e.Handled = true;

        // Instant scroll, no animation. The glide was implicated across
        // several rounds of reported lag and never conclusively cleared -
        // rather than keep chasing "smooth AND fast" under time pressure,
        // this removes the animation entirely. A plain ScrollToVerticalOffset
        // is about the cheapest, simplest thing WPF can do here, and it
        // guarantees there is no per-frame animation cost left to cause lag.
        double speed = SettingsService.Current.ScrollSpeed;
        if (speed <= 0 || double.IsNaN(speed)) speed = 1.0;
        speed = Math.Clamp(speed, 0.2, 3.0);

        double notches = e.Delta / 120.0;
        double delta = -notches * BaseStepPixels * speed;
        double target = Math.Clamp(sv.VerticalOffset + delta, 0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(target);
    }


    private static ComboBox? FindOpenComboBox(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox { IsDropDownOpen: true } cb) return cb;
            var found = FindOpenComboBox(child);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? FindDropDownScrollViewer(ComboBox cb)
    {
        var popup = FindVisualChild<Popup>(cb);
        if (popup?.Child == null) return null;
        return FindVisualChild<ScrollViewer>(popup.Child);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // --- Dropdown/popup scroll fix (self-contained, no per-ComboBox wiring) ---
    public static readonly DependencyProperty PopupScrollProperty =
        DependencyProperty.RegisterAttached("PopupScroll", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnPopupScrollChanged));

    public static void SetPopupScroll(DependencyObject o, bool v) => o.SetValue(PopupScrollProperty, v);
    public static bool GetPopupScroll(DependencyObject o) => (bool)o.GetValue(PopupScrollProperty);

    private static void OnPopupScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel -= PopupScrollViewer_PreviewMouseWheel;
            sv.PreviewMouseWheel += PopupScrollViewer_PreviewMouseWheel;
        }
        else
        {
            sv.PreviewMouseWheel -= PopupScrollViewer_PreviewMouseWheel;
        }
    }

    private static void PopupScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        double speed = SettingsService.Current.ScrollSpeed;
        if (speed <= 0 || double.IsNaN(speed)) speed = 1.0;
        speed = Math.Clamp(speed, 0.2, 3.0);

        double notches = e.Delta / 120.0;
        double delta = -notches * BaseStepPixels * speed;
        double current = sv.VerticalOffset;
        double target = Math.Clamp(current + delta, 0, Math.Max(0, sv.ExtentHeight - sv.ViewportHeight));
        sv.ScrollToVerticalOffset(target);
        e.Handled = true;
    }
}

