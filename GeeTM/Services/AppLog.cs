using System.IO;

namespace GeeTM.Services;

/// <summary>
/// Minimal append-only logger. If something goes wrong we want a trail on
/// disk, not a Windows Error Reporting popup. File is capped and rotated
/// so it can never grow unbounded on a machine that runs GeeTM for months.
/// </summary>
public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeeTM", "geetm.log");

    private static readonly object _lock = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB cap

    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    File.Delete(LogPath); // simple rotation ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â start fresh
                }

                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // If even logging fails (disk full, permissions), swallow it.
            // Logging must never be the thing that crashes the app.
        }
    }
}



