using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GeeTM.Services;

/// <summary>
/// Looks up the current public IP's abuse confidence score via AbuseIPDB.
/// Requires the user's own free API key (Settings > General) - AbuseIPDB's
/// free tier is generous (1000 checks/day) but is still a per-key limit, so
/// a key shared across every GeeTM install would get exhausted or banned
/// almost immediately. Off by default; only ever called when the user has
/// both enabled the feature and entered a key.
///
/// Same cadence as PublicIpService: cached, refreshed only occasionally (not
/// on every rotation tick), and keyed to the IP it was checked for - if the
/// IP changes, the cached score is discarded rather than shown against the
/// wrong address.
/// </summary>
public static class ThreatCheckService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private static DateTime _lastFetch = DateTime.MinValue;
    private static string? _lastCheckedIp;
    private static int? _score; // 0-100, AbuseIPDB's own confidence scale
    private static bool _fetchInFlight;
    private static bool _lastFetchFailed;

    /// <summary>Returns the last-known score for the given IP immediately
    /// (never blocks). Null means "not known yet" - callers should skip that
    /// rotation frame rather than show a wrong or placeholder value. Kicks
    /// off a background refresh if the cache is stale or the IP has
    /// changed since the last check.</summary>
    public static int? GetCachedScore(string currentIp)
    {
        if (string.IsNullOrEmpty(currentIp)) return null;

        bool ipChanged = !string.Equals(_lastCheckedIp, currentIp, StringComparison.Ordinal);
        bool stale = DateTime.UtcNow - _lastFetch > RefreshInterval;

        if ((ipChanged || stale) && !_fetchInFlight)
        {
            var s = SettingsService.Current;
            if (s.ThreatCheckEnabled && !string.IsNullOrWhiteSpace(s.AbuseIpDbApiKey))
            {
                _fetchInFlight = true;
                _ = RefreshAsync(currentIp, s.AbuseIpDbApiKey);
            }
        }

        return ipChanged ? null : _score;
    }

    private static async Task RefreshAsync(string ip, string apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.abuseipdb.com/api/v2/check?ipAddress={Uri.EscapeDataString(ip)}&maxAgeInDays=90");
            request.Headers.Add("Key", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // Common cases: 401 (bad key), 429 (rate limit hit) - both are
                // the user's own key/quota, not something we should retry
                // aggressively. Logged so it's diagnosable, not surfaced as
                // an error to the widget itself - the feature just quietly
                // has no score to show until the next refresh window.
                AppLog.Write($"ThreatCheckService: AbuseIPDB returned {(int)response.StatusCode} for {ip}.");
                _lastFetchFailed = true;
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("abuseConfidenceScore", out var scoreEl))
            {
                _score = scoreEl.GetInt32();
                _lastCheckedIp = ip;
                _lastFetch = DateTime.UtcNow;
                _lastFetchFailed = false;
            }
        }
        catch (Exception ex)
        {
            // No network, AbuseIPDB unreachable, malformed response, etc. -
            // best-effort enhancement, never something that should disrupt
            // the widget. Leaves the previous cached score (if any) in place.
            AppLog.Write($"ThreatCheckService.RefreshAsync failed: {ex.Message}");
            _lastFetchFailed = true;
        }
        finally
        {
            _fetchInFlight = false;
        }
    }

    /// <summary>Formats the short label suffix shown next to "IP:" when a
    /// score is available - e.g. " \u00b7 8%". Returns an empty string if
    /// the feature is off, unconfigured, or no score is cached yet for this
    /// IP, so callers can just concatenate it onto the label unconditionally.</summary>
    public static string GetLabelSuffix(string currentIp)
    {
        var s = SettingsService.Current;
        if (!s.ThreatCheckEnabled || string.IsNullOrWhiteSpace(s.AbuseIpDbApiKey)) return "";
        var score = GetCachedScore(currentIp);
        return score.HasValue ? $" \u00b7 {score.Value}%" : "";
    }
}
