using System.Net.NetworkInformation;

namespace GeeTM.Services;

/// <summary>
/// Detects VPN activity by checking for known VPN virtual network adapters
/// appearing or disappearing. This is a heuristic, not a certainty - some
/// VPN configurations don't create a recognisable virtual adapter, so a
/// "not detected" result never means "definitely no VPN," only "none of the
/// known patterns were seen." That's exactly why this fires as a transient,
/// dismissible notification about an observed change, not a persistent
/// status indicator claiming an ongoing state.
///
/// Off by default (VpnNotificationsEnabled) - this only runs its check loop
/// at all when the user has explicitly turned it on.
/// </summary>
public sealed class VpnDetectionService : IDisposable
{
    // Substring matches against adapter Name + Description, lowercased.
    // Covers the most common VPN client software's virtual adapters.
    private static readonly string[] VpnAdapterHints =
    {
        "wireguard", "openvpn", "tap-windows", "tap0901", "wintun",
        "nordlynx", "nordvpn", "expressvpn", "protonvpn", "surfshark",
        "cisco anyconnect", "anyconnect", "globalprotect", "fortinet",
        "checkpoint vpn", "pulse secure", "forticlient", "tunnelblick",
        "vpn adapter", "vpn client", "l2tp", "pptp adapter",
    };

    public event Action<bool>? VpnStateChanged; // true = connected, false = disconnected

    private readonly System.Threading.Timer _timer;
    private bool _lastKnownState;
    private bool _hasCheckedOnce;
    private volatile bool _disposed;

    public VpnDetectionService()
    {
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(8));
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTick(object? state)
    {
        // Same defensive pattern as NetworkMonitorService's timer callback:
        // this runs on a raw ThreadPool thread with no dispatcher and no
        // safety net, so it must never let an exception escape.
        try
        {
            if (_disposed) return;

            bool vpnPresent = false;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                string haystack = ($"{ni.Name} {ni.Description}").ToLowerInvariant();
                if (VpnAdapterHints.Any(hint => haystack.Contains(hint)))
                {
                    vpnPresent = true;
                    break;
                }
            }

            if (!_hasCheckedOnce)
            {
                // Don't fire a notification for the state observed at startup -
                // only for a genuine transition witnessed while running.
                _hasCheckedOnce = true;
                _lastKnownState = vpnPresent;
                return;
            }

            if (vpnPresent != _lastKnownState)
            {
                _lastKnownState = vpnPresent;
                VpnStateChanged?.Invoke(vpnPresent);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"VpnDetectionService tick failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _timer.Dispose(); } catch { /* best-effort teardown */ }
    }
}
