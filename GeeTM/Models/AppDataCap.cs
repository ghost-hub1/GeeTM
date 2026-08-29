namespace GeeTM.Models;

/// <summary>A user-configured daily data cap for one application, matched by
/// process name (so it applies across restarts of that app, not just one
/// specific process instance).</summary>
public class AppDataCap
{
    public string ProcessName { get; set; } = "";
    public long DailyCapBytes { get; set; }
}
