using System.Windows;

namespace GeeTM.Services;

public static class SkinManager
{
    public static readonly string[] AvailableSkins = { "Aurora", "Midnight", "Solar", "Mono" };

    public static void Apply(string skinName)
    {
        try
        {
            var name = AvailableSkins.Contains(skinName) ? skinName : "Aurora";
            var dict = new ResourceDictionary
            {
                Source = new Uri($"Views/Theme.{name}.xaml", UriKind.Relative)
            };

            var app = System.Windows.Application.Current;
            var merged = app.Resources.MergedDictionaries;

            // Replace rather than stack, so switching skins repeatedly never
            // leaks dictionaries or leaves a previous skin's colors bleeding through.
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source != null && merged[i].Source!.OriginalString.Contains("Theme."))
                {
                    merged.RemoveAt(i);
                }
            }
            merged.Add(dict);
        }
        catch (Exception ex)
        {
            AppLog.Write($"SkinManager.Apply({skinName}) failed: {ex.Message}");
        }
    }
}



