using System.Threading;
using System.Windows;
using GeeTM.Services;
using GeeTM.Views;
using Application = System.Windows.Application;

namespace GeeTM;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Global\\GeeTM_SingleInstance_9F3E2C7A1B4D", out bool createdNew);
        if (!createdNew)
        {
            AppLog.Write("Startup blocked: another GeeTM instance is already running. Exiting this copy.");
            Shutdown();
            return;
        }

        // AUTO-ADMIN: If per-process tracking is on (default) and we aren't admin, 
        // immediately request elevation before showing any UI.
        if (SettingsService.Current.ShowPerProcessBreakdown && !ElevationHelper.IsRunningElevated())
        {
            try
            {
                AppLog.Write("Auto-elevation triggered on startup.");
                AppRelauncher.RelaunchElevated();
                Shutdown();
                return;
            }
            catch (Exception ex)
            {
                AppLog.Write($"Auto-elevation failed or cancelled: {ex.Message}");
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"UI thread exception: {args.Exception}");
            args.Handled = true; 
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write($"Domain exception (fatal={args.IsTerminating}): {args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Views/Controls.xaml", UriKind.Relative)
        });

        LookManager.Apply(SettingsService.Current.UiLook);
        SkinManager.Apply(SettingsService.Current.Skin);

        var widget = new TaskbarWidget();
        widget.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}


