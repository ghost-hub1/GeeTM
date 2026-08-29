using System.IO;
using System.Text.Json;

namespace GeeTM.Services;

public class AppSettings
{
    // Defaults mapped to your preferred premium layout
    public string Skin { get; set; } = "Solar";
    public int PollIntervalMs { get; set; } = 1000;
    public bool UseBinaryUnits { get; set; } = false; 
    public bool StartWithWindows { get; set; } = false; 
    public string PreferredAdapter { get; set; } = ""; // Auto - picks the busiest active adapter
    public long DailyLimitBytes { get; set; } = 0; 
    public long MonthlyLimitBytes { get; set; } = 0;
    public bool ShowPerProcessBreakdown { get; set; } = true;
    public bool EmbedInTaskbar { get; set; } = false; // Kept false for public launch safety
    public int SchemaVersion { get; set; }
    public bool ShowSpeedInBits { get; set; } = false;   
    public bool HideWhenFullscreen { get; set; } = true;
    public bool ShowUploadRow { get; set; } = true;
    public bool ShowDownloadRow { get; set; } = true;
    public bool TodayShowsMonth { get; set; } = false;   
    public double ScrollSpeed { get; set; } = 1.0;       
    public int ChartWindowSeconds { get; set; } = 180;
    public int SpeedDecimalPlaces { get; set; } = 2;          
    public double WidgetOpacity { get; set; } = 1.0;         
    public string WidgetBackgroundHex { get; set; } = "#0B111C";
    public string WidgetFontFamily { get; set; } = "Trebuchet MS";
    public double WidgetFontSize { get; set; } = 12;
    public bool WidgetManualPosition { get; set; } = false;   
    public double WidgetX { get; set; } = 0;
    public double WidgetY { get; set; } = 0;
    public double WidgetOffsetX { get; set; } = 0;
    public double WidgetOffsetY { get; set; } = 0;
    public bool WidgetClickThrough { get; set; } = false;     
    public double WidgetCornerRadius { get; set; } = 6;
    public bool WidgetShadow { get; set; } = false;            
    public double WidgetWidth { get; set; } = 96;
    public double WidgetHeight { get; set; } = 39;
    public double WidgetIconTextGap { get; set; } = 0;        
    public double WidgetRowGap { get; set; } = 2.7;             
    public double WidgetDigitUnitGap { get; set; } = 4.3;       
    public bool WidgetDigitsBold { get; set; } = true;
    public bool WidgetUnitBold { get; set; } = false;
    public double WidgetPaddingH { get; set; } = 3.4;           
    public double WidgetPaddingV { get; set; } = 3.3;           
    public bool ShowTodayInWidget { get; set; } = true;
    public bool TotalBeforeSpeed { get; set; } = false;       
    public double WidgetGroupGap { get; set; } = 3;           
    public double TodayFontSize { get; set; } = 12;
    public double TodayPaddingH { get; set; } = 7.7;
    public double TodayPaddingV { get; set; } = 5.6;
    public string TodayLabelText { get; set; } = "Today: ";
    public double TodayDigitUnitGap { get; set; } = 3.4;
    public bool TodayDigitsBold { get; set; } = true;
    public bool TodayUnitBold { get; set; } = false;
    public WidgetColorMode ColorMode { get; set; } = WidgetColorMode.AutoDarker;
    public double AutoDarkenAmount { get; set; } = 0.45;

    // --- Rotating pill content (v5.0) ---
    // Master toggle: off by default, so nothing changes for existing users
    // until they explicitly turn it on.
    public bool RotatingPillEnabled { get; set; } = false;
    // Which pill hosts the rotation - "Today" or "Speed". Today is the
    // sensible default since it's the pill this whole idea grew out of.
    // Each rotating feature is assigned its own target pill independently -
    // "Speed" or "Today" - so IP and Flag can share a pill, sit on separate
    // ones, or either combination in between.
    public string IpTargetPill { get; set; } = "Today";
    public string FlagTargetPill { get; set; } = "Today";
    public int RotatingPillIntervalSeconds { get; set; } = 8;
    // Each content type is its own toggle, per the standing rule that every
    // feature gets an independent on/off switch - a user might want the
    // flag without the raw IP text (e.g. streaming their desktop and not
    // wanting to expose the literal address on screen), so these are
    // deliberately separate rather than one combined "show IP info" switch.
    public bool RotatePillShowIp { get; set; } = false;
    public bool RotatePillShowFlag { get; set; } = false;

    // --- VPN detection (v5.0) ---
    // Detection itself is a best-effort heuristic (recognising known VPN
    // virtual adapters appearing/disappearing) - not a certainty, which is
    // exactly why this surfaces as a transient notification about an
    // observed adapter change, not a persistent "VPN: ON/OFF" status claim.
    // Off by default.
    public bool VpnNotificationsEnabled { get; set; } = false;

    // --- IP threat check (v5.1) ---
    // Off by default - requires the user's own free AbuseIPDB API key, since
    // a shared/hardcoded key would get rate-limited or banned once more than
    // a handful of people used the app at once.
    public bool ThreatCheckEnabled { get; set; } = false;
    public string AbuseIpDbApiKey { get; set; } = "";

    // --- Pill border (v5.1) ---
    public bool PillBorderEnabled { get; set; } = false;
    public string PillBorderColorHex { get; set; } = "#FFFFFF";
    public double PillBorderThickness { get; set; } = 1.5;

    // --- Pill shape (v5.1) ---
    // "TwoPods": both pods fully rounded, independent - the original look.
    // "OnePod": outer corners rounded, inner (facing) corners square, making
    // the two pods read as one shape divided by the gap between them. When
    // PillBorderEnabled is on, the border automatically matches whichever
    // shape is selected here - this isn't a separate, independent setting.
    public string PillShapeStyle { get; set; } = "TwoPods";

    // --- Look (v5.2) - independent of the color Skin ---
    public string UiLook { get; set; } = "Classic";

    // --- Fullscreen overlay (v5.0) ---
    // Off by default. When on, a small always-on-top HUD replaces the normal
    // "just hide" behaviour while something else is fullscreen.
    // Off by default. When on, a brief opacity dip-and-recover softens the
    // moment rotated content swaps in embedded mode, mirroring the crossfade
    // floating mode already has. Kept as its own separate toggle from the
    // main rotation switch: embedded mode's rendering is lower-level than
    // floating mode's, so this needs to be something the user can verify is
    // smooth on their own machine and opt into deliberately, not something
    // that's just on by default.
    public bool EmbeddedFadeTransitionEnabled { get; set; } = false;
    public bool FullscreenOverlayEnabled { get; set; } = false;
}


public static class SettingsService
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeeTM", "settings.json");
    private static AppSettings? _cache;

    public static AppSettings Current
    {
        get
        {
            if (_cache != null) return _cache;
            _cache = Load();
            return _cache;
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null) return Migrate(settings);
            }
        }
        catch (Exception ex) { AppLog.Write($"SettingsService.Load failed: {ex.Message}"); }
        return new AppSettings();
    }

    public static string DataFolder => System.IO.Path.GetDirectoryName(Path_)!;

    public static AppSettings ResetToDefaults()
    {
        var fresh = new AppSettings();
        Save(fresh);
        return fresh;
    }

    private const int CurrentSchemaVersion = 4; 

    private static AppSettings Migrate(AppSettings settings)
    {
        bool changed = false;
        if (settings.SchemaVersion < 4)
        {
            var fresh = new AppSettings();
            settings.Skin = fresh.Skin;
            settings.WidgetFontFamily = fresh.WidgetFontFamily;
            settings.WidgetWidth = fresh.WidgetWidth;
            settings.WidgetHeight = fresh.WidgetHeight;
            settings.ColorMode = fresh.ColorMode;
            settings.AutoDarkenAmount = fresh.AutoDarkenAmount;
            settings.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }
        if (changed) Save(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            settings.SchemaVersion = CurrentSchemaVersion;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(Path_)) File.Replace(tmp, Path_, null);
            else File.Move(tmp, Path_);
            _cache = settings;
        }
        catch (Exception ex) { AppLog.Write($"SettingsService.Save failed: {ex.Message}"); }
    }
}



