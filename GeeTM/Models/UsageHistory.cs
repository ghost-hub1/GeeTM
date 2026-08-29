namespace GeeTM.Models;

/// <summary>One adapter's accumulated usage for a single day.</summary>
public class AdapterDayUsage
{
    public string AdapterName { get; set; } = "";
    // Best-effort classification: true if this adapter looks like a virtual/
    // local-only adapter (Docker, WSL, Hyper-V, loopback, etc.) rather than a
    // real physical connection to the internet. A heuristic, not a
    // guarantee - see UsageHistoryService for the actual classification.
    public bool IsLocal { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
}

/// <summary>One day's usage, broken down by adapter.</summary>
public class DayHistoryEntry
{
    public DateOnly Day { get; set; }
    public List<AdapterDayUsage> Adapters { get; set; } = new();
}
