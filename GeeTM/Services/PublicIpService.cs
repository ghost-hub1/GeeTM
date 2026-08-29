using System.Net.Http;
using System.Text.Json;

namespace GeeTM.Services;

/// <summary>
/// Looks up the machine's public-facing IP address and country, for the
/// rotating Today-pill content. Cached and refreshed only occasionally (not
/// on every rotation tick) - this is a monitoring tool, and a feature about
/// visibility into your own network shouldn't itself be chatty on the wire.
///
/// This makes an outbound HTTP request to a third-party IP lookup service.
/// That's disclosed plainly in the README and in the Settings description
/// for this feature - a network-focused app should be transparent about its
/// own network calls, especially one whose whole pitch includes security
/// awareness.
/// </summary>
public static class PublicIpService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    private static DateTime _lastFetch = DateTime.MinValue;
    private static string? _ip;
    private static string? _countryCode; // ISO 3166-1 alpha-2, e.g. "NG", "US", "GB"
    private static string? _countryName;
    private static bool _fetchInFlight;

    public readonly record struct IpInfo(string? Ip, string? CountryCode, string? CountryName);

    /// <summary>Returns the last-known IP/country immediately (never blocks),
    /// and kicks off a background refresh if the cache is stale. First call
    /// after launch will return an empty result while the first lookup is
    /// still in flight - callers should treat a null Ip as "not yet known"
    /// and simply skip that rotation frame, not show an error.</summary>
    public static IpInfo GetCached()
    {
        if (DateTime.UtcNow - _lastFetch > RefreshInterval && !_fetchInFlight)
        {
            _fetchInFlight = true;
            _ = RefreshAsync();
        }
        return new IpInfo(_ip, _countryCode, _countryName);
    }

    private static async Task RefreshAsync()
    {
        try
        {
            // ip-api.com's free tier returns IP + country in one request (no
            // separate geolocation call needed) and comfortably supports the
            // request rate this feature needs (one lookup every ~10 minutes,
            // not per rotation tick). Deliberately requesting only the three
            // fields actually used, not a full profile.
            var json = await _http.GetStringAsync("http://ip-api.com/json/?fields=status,countryCode,country,query");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() == "success")
            {
                _ip = root.TryGetProperty("query", out var q) ? q.GetString() : null;
                _countryCode = root.TryGetProperty("countryCode", out var cc) ? cc.GetString() : null;
                _countryName = root.TryGetProperty("country", out var cn) ? cn.GetString() : null;
                _lastFetch = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            // No network, lookup service unreachable, etc. - this is a
            // best-effort enhancement, never something that should disrupt
            // the widget. Leave the previous cached value in place (if any)
            // and just try again on the next refresh interval.
            AppLog.Write($"PublicIpService.RefreshAsync failed: {ex.Message}");
        }
        finally
        {
            _fetchInFlight = false;
        }
    }

    /// <summary>Converts an ISO 3166-1 alpha-2 country code to its flag emoji
    /// via Unicode regional indicator symbols. Reliable in floating mode
    /// (WPF/DirectWrite text rendering handles this correctly). NOT
    /// guaranteed reliable in embedded mode's native GDI+ renderer - multi
    /// -codepoint emoji sequences are a known weak spot there. Embedded mode
    /// should prefer the plain country code text unless flag rendering has
    /// been visually confirmed to work correctly on-device.</summary>
    public static string? CountryCodeToFlagEmoji(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2) return null;
        countryCode = countryCode.ToUpperInvariant();
        const int regionalIndicatorBase = 0x1F1E6; // 'A'
        int first = regionalIndicatorBase + (countryCode[0] - 'A');
        int second = regionalIndicatorBase + (countryCode[1] - 'A');
        return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
    }
}
