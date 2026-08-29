using System.Diagnostics;

namespace GeeTM.Services;

/// <summary>
/// Registers/unregisters GeeTM to launch at logon using schtasks, which
/// (unlike a plain registry Run key) survives elevation prompts cleanly
/// since the task itself can be marked to run with highest privileges ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â
/// important since per-process tracking needs admin.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "GeeTM_Autostart";

    public static bool IsRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            p!.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Write($"StartupManager.IsRegistered check failed: {ex.Message}");
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                var psi = new ProcessStartInfo("schtasks",
                    $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F")
                { CreateNoWindow = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                p!.WaitForExit(3000);
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks", $"/Delete /TN \"{TaskName}\" /F")
                { CreateNoWindow = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                p!.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"StartupManager.SetEnabled({enabled}) failed: {ex.Message}");
        }
    }
}



