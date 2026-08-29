namespace GeeTM.Models;

/// <summary>
/// One polled snapshot of network throughput. Immutable so it can be
/// safely handed across threads without locking.
/// </summary>
public readonly record struct NetSample(
    DateTime Timestamp,
    long TotalBytesReceived,
    long TotalBytesSent,
    double DownloadBytesPerSec,
    double UploadBytesPerSec,
    string AdapterName
);

/// <summary>
/// Rolling daily/monthly usage totals, persisted to disk so counters
/// survive restarts (a common TrafficMonitor pain point when it resets
/// unexpectedly on crash).
/// </summary>
public class UsageTotals
{
    public DateOnly Day { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public long BytesReceivedToday { get; set; }
    public long BytesSentToday { get; set; }
    public int MonthKey { get; set; } = DateTime.Now.Year * 100 + DateTime.Now.Month;
    public long BytesReceivedMonth { get; set; }
    public long BytesSentMonth { get; set; }
}



