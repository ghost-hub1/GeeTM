using System.IO;
using System.Text.Json;
using GeeTM.Models;

namespace GeeTM.Services;

/// <summary>
/// Tracks daily usage per application (by process name, not PID, so it
/// correctly follows an app across restarts) and fires an alert once when a
/// user-configured cap is crossed. Persisted with the same atomic-write and
/// throttled-save conventions as the other tracking services.
///
/// Built on top of ProcessNetworkService's per-PID totals rather than
/// replacing them: those totals never reset and are keyed by PID (a
/// restarted app gets a fresh PID), so this service does its own delta
/// tracking, summed across every PID sharing a process name at each tick -
/// that sum keeps growing correctly across process restarts, and the delta
/// against the previous tick is what actually accumulates into today's
/// total for that app name.
///
/// SetCap()/GetTodayUsage() are called from the UI thread (Dashboard), while
/// Accumulate() is called from the ETW processing thread - _stateLock
/// protects every access to the shared mutable state below so a cap being
/// added/removed can never race with a tick reading or writing it.
/// </summary>
public class DataCapService
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeeTM", "datacaps.json");

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

    private class PersistedState
    {
        public List<AppDataCap> Caps { get; set; } = new();
        public DateOnly Day { get; set; } = DateOnly.FromDateTime(DateTime.Now);
        public Dictionary<string, long> TodayUsage { get; set; } = new();
    }

    private readonly object _stateLock = new();
    private PersistedState _state;
    private readonly Dictionary<string, long> _lastSeenCumulative = new();
    private readonly HashSet<string> _alertedToday = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private readonly object _saveLock = new();

    /// <summary>processName, bytes used today, configured cap. Raised from
    /// whichever thread called Accumulate() - subscribers should marshal to
    /// the UI thread themselves if needed, same convention as the other
    /// services' events in this app.</summary>
    public event Action<string, long, long>? CapExceeded;

    public IReadOnlyList<AppDataCap> Caps
    {
        get { lock (_stateLock) { return new List<AppDataCap>(_state.Caps); } }
    }

    public DataCapService()
    {
        _state = Load();
    }

    public void SetCap(string processName, long dailyCapBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(processName)) return;
            lock (_stateLock)
            {
                var existing = _state.Caps.Find(c => string.Equals(c.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
                if (dailyCapBytes <= 0)
                {
                    if (existing != null) _state.Caps.Remove(existing);
                }
                else if (existing != null)
                {
                    existing.DailyCapBytes = dailyCapBytes;
                }
                else
                {
                    _state.Caps.Add(new AppDataCap { ProcessName = processName, DailyCapBytes = dailyCapBytes });
                }
            }
            Save();
        }
        catch (Exception ex)
        {
            AppLog.Write($"DataCapService.SetCap failed: {ex.Message}");
        }
    }

    public long GetTodayUsage(string processName)
    {
        try
        {
            lock (_stateLock)
            {
                return _state.TodayUsage.TryGetValue(processName, out var v) ? v : 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"DataCapService.GetTodayUsage failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Feed every ProcessNetworkService.Updated snapshot here.</summary>
    public void Accumulate(IReadOnlyList<ProcessNetUsage> list)
    {
        try
        {
            var alerts = new List<(string name, long used, long cap)>();

            lock (_stateLock)
            {
                RolloverIfNeeded_NoLock();
                if (_state.Caps.Count == 0) return; // nothing configured - skip the grouping work entirely

                var byName = new Dictionary<string, long>();
                foreach (var p in list)
                {
                    if (string.IsNullOrEmpty(p.ProcessName)) continue;
                    byName.TryGetValue(p.ProcessName, out var existing);
                    byName[p.ProcessName] = existing + p.BytesReceived + p.BytesSent;
                }

                bool changed = false;
                foreach (var (name, cumulative) in byName)
                {
                    if (!_lastSeenCumulative.TryGetValue(name, out var last))
                    {
                        _lastSeenCumulative[name] = cumulative;
                        continue;
                    }

                    long delta = cumulative - last;
                    _lastSeenCumulative[name] = cumulative;
                    if (delta <= 0) continue; // no growth this tick, or a counter reset - skip rather than risk a bogus negative

                    _state.TodayUsage.TryGetValue(name, out var todayExisting);
                    _state.TodayUsage[name] = todayExisting + delta;
                    changed = true;

                    var cap = _state.Caps.Find(c => string.Equals(c.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                    if (cap != null && cap.DailyCapBytes > 0 && !_alertedToday.Contains(name))
                    {
                        long used = _state.TodayUsage[name];
                        if (used >= cap.DailyCapBytes)
                        {
                            _alertedToday.Add(name);
                            alerts.Add((name, used, cap.DailyCapBytes));
                        }
                    }
                }

                if (changed && DateTime.UtcNow - _lastSaveUtc >= SaveInterval)
                {
                    // Save() takes its own lock and serialises _state, so it
                    // must run outside _stateLock to avoid a nested-lock
                    // ordering hazard; a snapshot copy taken here would add
                    // complexity for a throttled, best-effort write, so
                    // instead the flag below defers the actual Save() call
                    // to just after this lock releases.
                }
            }

            // Raise alerts and save outside the lock, so a slow subscriber
            // or a slow disk write can never hold up the next Accumulate()
            // call from the ETW thread.
            foreach (var (name, used, cap) in alerts) CapExceeded?.Invoke(name, used, cap);
            if (DateTime.UtcNow - _lastSaveUtc >= SaveInterval) Save();
        }
        catch (Exception ex)
        {
            AppLog.Write($"DataCapService.Accumulate failed: {ex.Message}");
        }
    }

    public void Flush() => Save();

    /// <summary>Caller must hold _stateLock.</summary>
    private void RolloverIfNeeded_NoLock()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_state.Day == today) return;

        _state.Day = today;
        _state.TodayUsage.Clear();
        _alertedToday.Clear();
        // _lastSeenCumulative is deliberately NOT cleared here - those values
        // track the ever-growing per-PID counters from ProcessNetworkService,
        // which don't reset at midnight. Clearing them would make the next
        // tick's delta include everything since app launch, wildly
        // overcounting the new day's first sample.
    }

    private static PersistedState Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var state = JsonSerializer.Deserialize<PersistedState>(json);
                if (state != null) return state;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"DataCapService.Load failed: {ex.Message}");
        }
        return new PersistedState();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                _lastSaveUtc = DateTime.UtcNow;
                string json;
                lock (_stateLock) { json = JsonSerializer.Serialize(_state); }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                var tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(Path_)) File.Replace(tmp, Path_, null);
                else File.Move(tmp, Path_);
            }
            catch (Exception ex)
            {
                AppLog.Write($"DataCapService.Save failed: {ex.Message}");
                try { var tmp = Path_ + ".tmp"; if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
