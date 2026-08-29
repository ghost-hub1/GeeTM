using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using GeeTM.Models;

namespace GeeTM.Services;

/// <summary>
/// Tracks usage per adapter per day, persisted to disk (same atomic
/// write-then-replace + throttled-save conventions as UsageTrackerService,
/// deliberately kept consistent rather than inventing a second approach).
/// Retains a bounded window of days so the file can't grow unbounded.
/// </summary>
public class UsageHistoryService
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeeTM", "history.json");

    private const int RetentionDays = 90;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);

    private List<DayHistoryEntry> _entries;
    private readonly Dictionary<string, (long r, long s)> _baselines = new();
    private readonly HashSet<string> _classifiedAdapters = new();
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private readonly object _saveLock = new();

    // Same substring-based heuristic family used by VpnDetectionService, but
    // for the opposite question: does this adapter look like a virtual/local
    // -only one rather than a real connection to the internet? A "not
    // local" result never means "definitely internet-bound" - only that none
    // of these patterns matched.
    private static readonly string[] LocalAdapterHints =
    {
        "virtual", "docker", "wsl", "hyper-v", "vethernet", "loopback",
        "vmware", "virtualbox", "hosted network", "bluetooth", "direct",
        "npcap", "teredo", "isatap",
    };

    public UsageHistoryService()
    {
        _entries = Load();
    }

    /// <summary>Feed every NetSample here, same as UsageTrackerService -
    /// converts cumulative adapter counters into deltas, keyed per adapter
    /// name, so an adapter swap or reset can't corrupt another adapter's
    /// running total.</summary>
    public void Accumulate(NetSample sample)
    {
        try
        {
            string adapterName = sample.AdapterName;
            if (string.IsNullOrEmpty(adapterName)) return;

            var today = DateOnly.FromDateTime(DateTime.Now);
            var entry = _entries.Find(e => e.Day == today);
            if (entry == null)
            {
                entry = new DayHistoryEntry { Day = today };
                _entries.Add(entry);
                PruneOld();
            }

            if (!_baselines.TryGetValue(adapterName, out var baseline))
            {
                // First sample seen for this adapter (ever, or since a
                // restart) - establish the baseline, don't count a delta yet.
                _baselines[adapterName] = (sample.TotalBytesReceived, sample.TotalBytesSent);
                return;
            }

            long deltaR = sample.TotalBytesReceived - baseline.r;
            long deltaS = sample.TotalBytesSent - baseline.s;
            _baselines[adapterName] = (sample.TotalBytesReceived, sample.TotalBytesSent);

            // Negative delta = adapter reset/rollover - skip this tick's
            // contribution rather than corrupting the running total, same
            // rule UsageTrackerService already applies.
            if (deltaR < 0 || deltaS < 0) return;

            var adapterEntry = entry.Adapters.Find(a => a.AdapterName == adapterName);
            if (adapterEntry == null)
            {
                adapterEntry = new AdapterDayUsage
                {
                    AdapterName = adapterName,
                    IsLocal = ClassifyLocal(adapterName)
                };
                entry.Adapters.Add(adapterEntry);
            }
            adapterEntry.BytesReceived += deltaR;
            adapterEntry.BytesSent += deltaS;

            if (DateTime.UtcNow - _lastSaveUtc >= SaveInterval) Save();
        }
        catch (Exception ex)
        {
            AppLog.Write($"UsageHistoryService.Accumulate failed: {ex.Message}");
        }
    }

    public void Flush() => Save();

    /// <summary>Most recent entries, newest first, up to the requested count.</summary>
    public List<DayHistoryEntry> GetRecentHistory(int days)
    {
        try
        {
            var sorted = new List<DayHistoryEntry>(_entries);
            sorted.Sort((a, b) => b.Day.CompareTo(a.Day));
            return sorted.Count > days ? sorted.GetRange(0, days) : sorted;
        }
        catch (Exception ex)
        {
            AppLog.Write($"UsageHistoryService.GetRecentHistory failed: {ex.Message}");
            return new List<DayHistoryEntry>();
        }
    }

    private bool ClassifyLocal(string adapterName)
    {
        try
        {
            // Only looked up once per newly-seen adapter, not per tick - a
            // NetworkInterface enumeration is cheap but there's no reason to
            // repeat it for an adapter already classified.
            string haystack = adapterName.ToLowerInvariant();
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name == adapterName);
            if (ni != null) haystack += " " + ni.Description.ToLowerInvariant();

            return LocalAdapterHints.Any(hint => haystack.Contains(hint));
        }
        catch (Exception ex)
        {
            AppLog.Write($"UsageHistoryService.ClassifyLocal failed: {ex.Message}");
            return false; // unclassifiable - default to "not local" (i.e. treat as internet-bound), the safer assumption for a monitoring tool
        }
    }

    private void PruneOld()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-RetentionDays);
        _entries.RemoveAll(e => e.Day < cutoff);
    }

    private static List<DayHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var entries = JsonSerializer.Deserialize<List<DayHistoryEntry>>(json);
                if (entries != null) return entries;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"UsageHistoryService.Load failed: {ex.Message}");
        }
        return new List<DayHistoryEntry>();
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                _lastSaveUtc = DateTime.UtcNow;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                var json = JsonSerializer.Serialize(_entries);

                var tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(Path_)) File.Replace(tmp, Path_, null);
                else File.Move(tmp, Path_);
            }
            catch (Exception ex)
            {
                AppLog.Write($"UsageHistoryService.Save failed: {ex.Message}");
                try { var tmp = Path_ + ".tmp"; if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
