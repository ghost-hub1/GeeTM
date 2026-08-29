using System.Windows;

namespace GeeTM.Services;

/// <summary>
/// Small attached property that drives whether a sidebar nav button shows its
/// text label. Set it on each nav Button; the NavButton template binds its
/// label ContentPresenter visibility to it. Kept out of the Dashboard window
/// so the shared Controls.xaml template can reference it cleanly through the
/// existing svc: namespace, rather than cross-referencing a specific Window.
/// </summary>
public static class NavState
{
    public static readonly DependencyProperty LabelVisibilityProperty =
        DependencyProperty.RegisterAttached(
            "LabelVisibility", typeof(Visibility), typeof(NavState),
            new PropertyMetadata(Visibility.Visible));

    public static void SetLabelVisibility(DependencyObject o, Visibility v) => o.SetValue(LabelVisibilityProperty, v);
    public static Visibility GetLabelVisibility(DependencyObject o) => (Visibility)o.GetValue(LabelVisibilityProperty);
}


