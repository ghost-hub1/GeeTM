namespace GeeTM.Services;

/// <summary>
/// Single source of truth for turning byte counts into display strings.
///
/// This exists because `UseBinaryUnits` was a saved, user-facing setting that
/// nothing ever read ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â every formatter in the app divided by 1024 and hard-coded
/// "KB/s" regardless. Speed and total formatting were also duplicated in three
/// places (widget, dashboard, process list) and had already drifted apart.
/// </summary>
public static class UnitFormatter
{
    private static AppSettings S => SettingsService.Current;

    /// <summary>Speed as (value, unit). Honours binary/decimal units and the
    /// bits-per-second display mode (Mbps, the convention ISPs quote).</summary>
    public static (string Value, string Unit) Speed(double bytesPerSec)
    {
        int decimals = S.SpeedDecimalPlaces is 1 or 2 ? S.SpeedDecimalPlaces : 2;
        string fmt = decimals == 1 ? "0.0" : "0.00";

        if (S.ShowSpeedInBits)
        {
            // Bit rates are decimal by universal convention - 1 Mbps is
            // 1,000,000 bits, never 1,048,576. Binary units are deliberately
            // not applied here.
            double bits = bytesPerSec * 8.0;
            if (bits < 1_000_000) return ((bits / 1_000.0).ToString(fmt), "Kbps");
            if (bits < 1_000_000_000) return ((bits / 1_000_000.0).ToString(fmt), "Mbps");
            return ((bits / 1_000_000_000.0).ToString(fmt), "Gbps");
        }

        double b = S.UseBinaryUnits ? 1024.0 : 1000.0;
        string k = S.UseBinaryUnits ? "KiB/s" : "KB/s";
        string m = S.UseBinaryUnits ? "MiB/s" : "MB/s";
        string g = S.UseBinaryUnits ? "GiB/s" : "GB/s";

        double kv = bytesPerSec / b;
        if (kv < b) return (kv.ToString(fmt), k);
        double mv = kv / b;
        if (mv < b) return (mv.ToString(fmt), m);
        return ((mv / b).ToString(fmt), g);
    }

    /// <summary>Cumulative total as (value, unit). Always in bytes - a data cap
    /// is quoted in gigabytes, never gigabits, so bits mode does not apply.</summary>
    public static (string Value, string Unit) Total(long bytes)
    {
        double b = S.UseBinaryUnits ? 1024.0 : 1000.0;
        string m = S.UseBinaryUnits ? "MiB" : "MB";
        string g = S.UseBinaryUnits ? "GiB" : "GB";
        string t = S.UseBinaryUnits ? "TiB" : "TB";

        double mv = bytes / b / b;
        if (mv < b) return (mv.ToString("0.0"), m);
        double gv = mv / b;
        if (gv < b) return (gv.ToString("0.00"), g);
        return ((gv / b).ToString("0.00"), t);
    }

    public static string SpeedString(double bytesPerSec)
    {
        var (v, u) = Speed(bytesPerSec);
        return $"{v} {u}";
    }

    public static string TotalString(long bytes)
    {
        var (v, u) = Total(bytes);
        return $"{v} {u}";
    }

    /// <summary>Axis label for the dashboard chart, matching whatever unit mode
    /// the chart values were scaled into.</summary>
    public static string ChartUnitLabel()
        => S.ShowSpeedInBits ? "Kbps" : (S.UseBinaryUnits ? "KiB/s" : "KB/s");

    /// <summary>Scales a raw byte rate into the chart's plotting unit.</summary>
    public static double ToChartUnit(double bytesPerSec)
        => S.ShowSpeedInBits ? (bytesPerSec * 8.0 / 1000.0) : (bytesPerSec / (S.UseBinaryUnits ? 1024.0 : 1000.0));

    /// <summary>Bytes for a limit expressed in GB/GiB in the settings UI.</summary>
    public static long GigabytesToBytes(double gb)
    {
        double b = S.UseBinaryUnits ? 1024.0 : 1000.0;
        return (long)(gb * b * b * b);
    }

    public static double BytesToGigabytes(long bytes)
    {
        double b = S.UseBinaryUnits ? 1024.0 : 1000.0;
        return bytes / b / b / b;
    }
}



