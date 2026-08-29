using System.Diagnostics;
using GeeTM.Models;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace GeeTM.Services;

/// <summary>
/// Attributes network bytes to individual processes using the built-in
/// Windows ETW kernel network provider ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â the same mechanism GlassWire and
/// NetLimiter use. Requires admin (see app.manifest). Every failure mode
/// (session already in use, insufficient privilege, provider unavailable
/// on older Windows builds) degrades gracefully: the rest of GeeTM keeps
/// working with total-only stats instead of crashing.
/// </summary>
public class ProcessNetworkService : IDisposable
{
    private const string SessionName = "GeeTM-NetTrace";
    private TraceEventSession? _session;
    private Task? _processingTask;
    private readonly Dictionary<int, ProcessNetUsage> _totals = new();
    private readonly Dictionary<int, (long r, long s, DateTime t)> _lastTick = new();
    private readonly object _lock = new();
    private bool _available;

    public event Action<IReadOnlyList<ProcessNetUsage>>? Updated;
    public bool IsAvailable => _available;
    public string? UnavailableReason { get; private set; }

    public void Start()
    {
        try
        {
            // Clear any orphaned session from a previous crash before starting fresh.
            try { TraceEventSession.GetActiveSession(SessionName)?.Dispose(); } catch { /* ignore */ }

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            _session.Source.Kernel.TcpIpRecv += data => Record(data.ProcessID, received: data.size, sent: 0);
            _session.Source.Kernel.TcpIpSend += data => Record(data.ProcessID, received: 0, sent: data.size);
            _session.Source.Kernel.UdpIpRecv += data => Record(data.ProcessID, received: data.size, sent: 0);
            _session.Source.Kernel.UdpIpSend += data => Record(data.ProcessID, received: 0, sent: data.size);

            _available = true;
            _processingTask = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex)
                {
                    AppLog.Write($"ETW session processing ended: {ex.Message}");
                    _available = false;
                }
            });

            // Emit an aggregated snapshot on its own cadence, decoupled from
            // the raw event flood, so UI updates stay smooth under heavy traffic.
            _ = Task.Run(async () =>
            {
                while (_available)
                {
                    await Task.Delay(1000);
                    Emit();
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
            UnavailableReason = "Per-process breakdown needs admin rights. Restart GeeTM as administrator to enable it.";
            _available = false;
            AppLog.Write("ProcessNetworkService: not elevated, per-process breakdown disabled.");
        }
        catch (Exception ex)
        {
            UnavailableReason = "Per-process breakdown unavailable on this system.";
            _available = false;
            AppLog.Write($"ProcessNetworkService.Start failed: {ex.Message}");
        }
    }

    private void Record(int pid, long received, long sent)
    {
        if (pid <= 0) return;
        lock (_lock)
        {
            if (!_totals.TryGetValue(pid, out var usage))
            {
                usage = new ProcessNetUsage { Pid = pid, ProcessName = ResolveName(pid) };
                _totals[pid] = usage;
            }
            usage.BytesReceived += received;
            usage.BytesSent += sent;
        }
    }

    private void Emit()
    {
        List<ProcessNetUsage> snapshot;
        var now = DateTime.Now;
        lock (_lock)
        {
            snapshot = new List<ProcessNetUsage>(_totals.Count);
            foreach (var kv in _totals)
            {
                var (lastR, lastS, lastT) = _lastTick.TryGetValue(kv.Key, out var prev) ? prev : (0L, 0L, now);
                double elapsed = Math.Max((now - lastT).TotalSeconds, 0.5);
                var usage = kv.Value;
                usage.DownloadBytesPerSec = Math.Max(0, (usage.BytesReceived - lastR) / elapsed);
                usage.UploadBytesPerSec = Math.Max(0, (usage.BytesSent - lastS) / elapsed);
                _lastTick[kv.Key] = (usage.BytesReceived, usage.BytesSent, now);
                snapshot.Add(new ProcessNetUsage
                {
                    Pid = usage.Pid, ProcessName = usage.ProcessName,
                    BytesReceived = usage.BytesReceived, BytesSent = usage.BytesSent,
                    DownloadBytesPerSec = usage.DownloadBytesPerSec, UploadBytesPerSec = usage.UploadBytesPerSec
                });
            }
        }
        snapshot.Sort((a, b) => (b.DownloadBytesPerSec + b.UploadBytesPerSec).CompareTo(a.DownloadBytesPerSec + a.UploadBytesPerSec));
        Updated?.Invoke(snapshot.Take(15).ToList()); // top 15 keeps the UI list readable
    }

    private static string ResolveName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return $"pid:{pid}"; } // process may have already exited
    }

    public void Dispose()
    {
        _available = false;
        try { _session?.Dispose(); } catch { /* best-effort teardown */ }
    }
}



