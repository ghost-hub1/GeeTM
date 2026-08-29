using System.IO;
using System.Text.Json;
using GeeTM.Models;

namespace GeeTM.Services;

/// <summary>
/// Tracks cumulative bytes for the day and month, persisted to disk after
/// every update so a crash, reboot, or Windows update never loses the
/// running total ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â a real, frequently-reported TrafficMonitor pain point.
/// Rolls over automatically at midnight / month boundary.
/// </summary>
public class UsageTrackerService
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeeTM", "usage.json");

    public UsageTotals Totals { get; private set; }
    public event Action<UsageTotals>? LimitExceeded;

    private long _lastReceived;
    private long _lastSent;
    private bool _baselineSet;

    // Save throttling. The old code called Save() on EVERY sample, which with
    // the default 1000 ms poll meant a full JSON serialise + File.WriteAllText
    // once per second, forever, from a thread-pool thread. That is a constant
    // background disk write for a widget that displays two numbers. Totals are
    // now flushed at most every 15 s, plus immediately on rollover and on exit,
    // so at worst 15 s of counting is lost in a hard crash.
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private readonly object _saveLock = new();

    // One-shot alert latches. CheckLimits() previously raised LimitExceeded on
    // every single tick once the total was over the cap, which fired a tray
    // balloon once per second until midnight.
    private DateOnly _dailyAlertShownFor = DateOnly.MinValue;
    private int _monthlyAlertShownFor = -1;

    public UsageTrackerService()
    {
        Totals = Load();
    }

    /// <summary>Feed every NetSample here; internally converts cumulative
    /// adapter counters into deltas so adapter swaps don't double-count.</summary>
    public void Accumulate(NetSample sample)
    {
        RolloverIfNeeded();

        if (!_baselineSet)
        {
            _lastReceived = sample.TotalBytesReceived;
            _lastSent = sample.TotalBytesSent;
            _baselineSet = true;
            return;
        }

        long deltaR = sample.TotalBytesReceived - _lastReceived;
        long deltaS = sample.TotalBytesSent - _lastSent;

        // Negative delta = adapter reset/rollover/swap ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â skip this tick's
        // contribution rather than corrupting the running total.
        if (deltaR >= 0) { Totals.BytesReceivedToday += deltaR; Totals.BytesReceivedMonth += deltaR; }
        if (deltaS >= 0) { Totals.BytesSentToday += deltaS; Totals.BytesSentMonth += deltaS; }

        _lastReceived = sample.TotalBytesReceived;
        _lastSent = sample.TotalBytesSent;

        CheckLimits();

        if (DateTime.UtcNow - _lastSaveUtc >= SaveInterval) Save();
    }

    /// <summary>Forces an immediate flush. Called on exit so the last few
    /// seconds of counting are not lost to the save throttle.</summary>
    public void Flush() => Save();

    private void RolloverIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var monthKey = DateTime.Now.Year * 100 + DateTime.Now.Month;

        bool rolled = false;

        if (Totals.Day != today)
        {
            Totals.Day = today;
            Totals.BytesReceivedToday = 0;
            Totals.BytesSentToday = 0;
            _dailyAlertShownFor = DateOnly.MinValue; // a new day gets a fresh alert
            rolled = true;
        }
        if (Totals.MonthKey != monthKey)
        {
            Totals.MonthKey = monthKey;
            Totals.BytesReceivedMonth = 0;
            Totals.BytesSentMonth = 0;
            _monthlyAlertShownFor = -1;
            rolled = true;
        }

        // A rollover must never be lost to the save throttle - it is the one
        // moment where dropping the write would resurrect yesterday's total.
        if (rolled) Save();
    }

    private void CheckLimits()
    {
        var s = SettingsService.Current;
        long todayTotal = Totals.BytesReceivedToday + Totals.BytesSentToday;
        long monthTotal = Totals.BytesReceivedMonth + Totals.BytesSentMonth;

        if (s.DailyLimitBytes > 0 && todayTotal > s.DailyLimitBytes && _dailyAlertShownFor != Totals.Day)
        {
            _dailyAlertShownFor = Totals.Day;
            LimitExceeded?.Invoke(Totals);
            return;
        }

        if (s.MonthlyLimitBytes > 0 && monthTotal > s.MonthlyLimitBytes && _monthlyAlertShownFor != Totals.MonthKey)
        {
            _monthlyAlertShownFor = Totals.MonthKey;
            LimitExceeded?.Invoke(Totals);
        }
    }

    private static UsageTotals Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var totals = JsonSerializer.Deserialize<UsageTotals>(json);
                if (totals != null) return totals;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"UsageTrackerService.Load failed: {ex.Message}");
        }
        return new UsageTotals();
    }

    private void Save()
    {
        // Accumulate() runs on a thread-pool timer thread; a lock keeps a
        // throttled write from colliding with an explicit Flush() from the UI
        // thread and producing a truncated usage.json.
        lock (_saveLock)
        {
            try
            {
                _lastSaveUtc = DateTime.UtcNow;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);

                // Write-then-replace so a crash mid-write can never leave a
                // half-written usage.json that fails to parse on next launch.
                var tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Totals));
                File.Move(tmp, Path_, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"UsageTrackerService.Save failed: {ex.Message}");
            }
        }
    }
}



