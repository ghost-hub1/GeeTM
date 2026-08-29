using System.Diagnostics;

namespace GeeTM.Services;

public static class AppRelauncher
{
    /// <summary>Relaunches the app elevated (triggers a UAC prompt) and exits
    /// the current instance. Needed for per-process tracking, which requires
    /// admin ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â and which is mutually exclusive with taskbar embedding.</summary>
    public static void RelaunchElevated()
    {
        Relaunch(runas: true);
    }

    /// <summary>Relaunches the app unelevated, dropping admin rights so
    /// taskbar embedding becomes possible again.</summary>
    public static void RelaunchStandard()
    {
        Relaunch(runas: false);
    }

    private static void Relaunch(bool runas)
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                AppLog.Write("AppRelauncher: could not determine exe path, aborting relaunch.");
                return;
            }

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = runas ? "runas" : ""
            };
            Process.Start(psi);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            // Most common cause: the user cancelled the UAC prompt. That's
            // not an error worth crashing over ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â just stay running as-is.
            AppLog.Write($"AppRelauncher.Relaunch(runas={runas}) failed or was cancelled: {ex.Message}");
        }
    }
}



