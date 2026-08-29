using System.Net.NetworkInformation;
using GeeTM.Models;

namespace GeeTM.Services;

/// <summary>
/// Polls network adapters for throughput. Designed around one rule:
/// a single bad reading (adapter disconnects, VPN drops, laptop sleeps)
/// must never take the whole app down. Every failure point is isolated
/// and self-heals on the next tick instead of throwing upward.
/// </summary>
public class NetworkMonitorService : IDisposable
{
    public event Action<NetSample>? SampleReady;
    public event Action<string>? AdapterChanged;

    private readonly System.Threading.Timer _timer;
    private readonly object _tickLock = new();
    private bool _tickInFlight;
    private bool _disposed;

    private NetworkInterface? _activeAdapter;
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private DateTime _lastSampleTime;
    private DateTime _lastAdapterRefresh = DateTime.MinValue;
    private int _consecutiveStatsFailures;
    private const int MaxConsecutiveFailuresBeforeFallback = 3;

    public string PreferredAdapterName { get; set; } = PreferWifiSentinel;
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>Sentinel meaning "prefer the Wi-Fi adapter if one is up,
    /// otherwise fall back to the busiest-adapter auto logic." This is the
    /// app's default, distinct from the empty string which means pure
    /// "auto, busiest adapter" with no Wi-Fi preference.</summary>
    public const string PreferWifiSentinel = "__wifi__";

    public NetworkMonitorService()
    {
        // Timer callback runs on a thread-pool thread, not the UI thread,
        // so a slow WMI/adapter call never freezes the taskbar widget.
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        RefreshActiveAdapter();
        _timer.Change(0, PollIntervalMs);
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    private void OnTick(object? state)
    {
        // Reentrancy guard: if a previous tick is still running (e.g. system
        // woke from sleep and NetworkInterface enumeration stalls), skip this
        // tick instead of piling up threads.
        lock (_tickLock)
        {
            if (_tickInFlight) return;
            _tickInFlight = true;
        }

        try
        {
            // Adapters can appear/disappear (VPN, USB tether, docking station).
            // Re-check every 10s rather than every tick to keep CPU usage minimal.
            if ((DateTime.Now - _lastAdapterRefresh).TotalSeconds > 10)
            {
                RefreshActiveAdapter();
            }

            if (_activeAdapter is null)
            {
                return; // no adapters up ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â just wait for the next tick
            }

            IPv4InterfaceStatistics stats;
            try
            {
                stats = _activeAdapter.GetIPv4Statistics();
                _consecutiveStatsFailures = 0;
            }
            catch (Exception)
            {
                _consecutiveStatsFailures++;
                AppLog.Write($"GetIPv4Statistics failed for '{_activeAdapter.Name}' ({_consecutiveStatsFailures} in a row).");

                // A genuinely disconnected Wi-Fi adapter with no IPv4 lease
                // is expected to fail here occasionally ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â show 0.00 and move
                // on. But if it fails repeatedly while a preference like
                // "prefer Wi-Fi" is active, that's a strong signal this
                // specific adapter object just isn't going to produce
                // readable stats right now. Rather than staying stuck showing
                // 0.00 forever, fall back to picking the busiest adapter for
                // now ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the next scheduled refresh will try the preferred
                // adapter again, so it recovers automatically once it's usable.
                if (_consecutiveStatsFailures >= MaxConsecutiveFailuresBeforeFallback
                    && PreferredAdapterName == PreferWifiSentinel)
                {
                    AppLog.Write("Wi-Fi adapter unreadable after repeated attempts ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â falling back to busiest adapter for now.");
                    _consecutiveStatsFailures = 0;
                    _lastAdapterRefresh = DateTime.MinValue; // force a fresh selection next tick
                    FallBackToBusiestAdapter();
                }

                SampleReady?.Invoke(new NetSample(
                    Timestamp: DateTime.Now, TotalBytesReceived: _lastBytesReceived, TotalBytesSent: _lastBytesSent,
                    DownloadBytesPerSec: 0, UploadBytesPerSec: 0, AdapterName: _activeAdapter.Name));
                return;
            }

            var now = DateTime.Now;
            long bytesReceived = stats.BytesReceived;
            long bytesSent = stats.BytesSent;

            if (_lastSampleTime != default)
            {
                double elapsed = Math.Max((now - _lastSampleTime).TotalSeconds, 0.001);

                // Counters can roll over on some adapters after driver resets;
                // treat any negative delta as "adapter reset" rather than crash math.
                long deltaDown = bytesReceived - _lastBytesReceived;
                long deltaUp = bytesSent - _lastBytesSent;
                double down = deltaDown >= 0 ? deltaDown / elapsed : 0;
                double up = deltaUp >= 0 ? deltaUp / elapsed : 0;

                SampleReady?.Invoke(new NetSample(
                    Timestamp: now,
                    TotalBytesReceived: bytesReceived,
                    TotalBytesSent: bytesSent,
                    DownloadBytesPerSec: down,
                    UploadBytesPerSec: up,
                    AdapterName: _activeAdapter.Name
                ));
            }

            _lastBytesReceived = bytesReceived;
            _lastBytesSent = bytesSent;
            _lastSampleTime = now;
        }
        catch (Exception ex)
        {
            // Last-resort net: log and carry on. This is the single most
            // important line in the file for the "never crashes" promise.
            AppLog.Write($"NetworkMonitorService tick failed: {ex.Message}");
        }
        finally
        {
            lock (_tickLock) { _tickInFlight = false; }
        }
    }

    private void FallBackToBusiestAdapter()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            var chosen = candidates
                .OrderByDescending(n =>
                {
                    try { var s = n.GetIPv4Statistics(); return s.BytesReceived + s.BytesSent; }
                    catch { return 0L; }
                })
                .FirstOrDefault();

            if (chosen != null)
            {
                _activeAdapter = chosen;
                _lastSampleTime = default;
                AdapterChanged?.Invoke(chosen.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"FallBackToBusiestAdapter failed: {ex.Message}");
        }
    }

    private void RefreshActiveAdapter()
    {
        _lastAdapterRefresh = DateTime.Now;
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            NetworkInterface? chosen = null;

            if (PreferredAdapterName == PreferWifiSentinel)
            {
                var allWifi = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    .ToList();

                // Windows often exposes virtual/hosted-network adapters
                // alongside the real physical Wi-Fi adapter (e.g. "Microsoft
                // Wi-Fi Direct Virtual Adapter", hosted-network miniports).
                // These report as Wireless80211 too but never carry real
                // traffic. Picking the first match without filtering these
                // out was the actual bug behind "always shows 0.00 KB/s" ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â
                // it could easily land on one of these instead of the real one.
                bool LooksVirtual(NetworkInterface n) =>
                    n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    n.Description.Contains("Direct", StringComparison.OrdinalIgnoreCase) ||
                    n.Description.Contains("Hosted", StringComparison.OrdinalIgnoreCase) ||
                    n.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

                var realWifi = allWifi.Where(n => !LooksVirtual(n)).ToList();

                // Prefer a real, currently-up adapter, picking the busiest if
                // more than one somehow qualifies (multiple physical Wi-Fi
                // cards, unusual but possible).
                chosen = realWifi
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .OrderByDescending(n =>
                    {
                        try { var s = n.GetIPv4Statistics(); return s.BytesReceived + s.BytesSent; }
                        catch { return 0L; }
                    })
                    .FirstOrDefault();

                // Disconnected but still real hardware: stay on it (0 KB/s
                // display) rather than falling through to some other adapter.
                chosen ??= realWifi.FirstOrDefault();

                // Absolute last resort: even a virtual-looking one is better
                // than nothing if that's truly all this machine reports.
                chosen ??= allWifi.FirstOrDefault();
            }
            else if (!string.IsNullOrEmpty(PreferredAdapterName))
            {
                chosen = candidates.FirstOrDefault(n => n.Name == PreferredAdapterName);
            }

            // Auto mode: pick whichever adapter has moved the most bytes,
            // so a sleeping Ethernet port doesn't win over active Wi-Fi.
            chosen ??= candidates
                .OrderByDescending(n =>
                {
                    try { var s = n.GetIPv4Statistics(); return s.BytesReceived + s.BytesSent; }
                    catch { return 0L; }
                })
                .FirstOrDefault();

            if (chosen?.Id != _activeAdapter?.Id)
            {
                _activeAdapter = chosen;
                _lastSampleTime = default; // reset delta baseline on adapter swap
                _consecutiveStatsFailures = 0;
                if (chosen != null)
                {
                    AppLog.Write($"Active adapter selected: {chosen.Name} (preference: {PreferredAdapterName})");
                    AdapterChanged?.Invoke(chosen.Name);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"RefreshActiveAdapter failed: {ex.Message}");
            _activeAdapter = null;
        }
    }

    public static List<string> GetAvailableAdapterNames()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.Name)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}



