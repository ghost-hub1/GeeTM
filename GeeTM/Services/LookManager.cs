using System.Windows;

namespace GeeTM.Services;

/// <summary>
/// Applies the "Look" (Classic or Premium) - an axis independent of the
/// color Skin (Aurora/Midnight/Solar/Mono). Unlike SkinManager, this is
/// additive rather than a replacement: Controls.xaml (the base styles) is
/// always loaded once at startup and never touched here. "Premium" means
/// layering Controls.Premium.xaml on top of it; "Classic" means making sure
/// that overlay isn't present. Order matters for WPF resource lookup - the
/// overlay is added AFTER the base dictionary, so its keys take precedence
/// for the specific styles it overrides, while everything else still comes
/// from the base dictionary unchanged.
/// </summary>
public static class LookManager
{
    public static void Apply(string look)
    {
        try
        {
            var app = Application.Current;
            var merged = app.Resources.MergedDictionaries;

            // Remove any existing overlay first, so toggling back and forth
            // never stacks duplicates or leaves a stale copy behind.
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source != null && merged[i].Source!.OriginalString.Contains("Controls.Premium"))
                {
                    merged.RemoveAt(i);
                }
            }

            if (look == "Premium")
            {
                merged.Add(new ResourceDictionary
                {
                    Source = new Uri("Views/Controls.Premium.xaml", UriKind.Relative)
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"LookManager.Apply({look}) failed: {ex.Message}");
        }
    }
}
