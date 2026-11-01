using System.Windows;

namespace LightBrowser;

public partial class App : Application
{
    SingleInstance? _instance;
    readonly ErrorBudget _errors = new(8, TimeSpan.FromSeconds(60));
    bool _told, _handling;

    // Normal start opens the main window. "--check-sites <in.json> <out.json>" runs the site checker instead.
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiError;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog.Write("fatal (background thread)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { CrashLog.Write("background task", a.Exception); a.SetObserved(); };

        base.OnStartup(e);
        var args = e.Args;
        var at = Array.IndexOf(args, "--check-sites");
        if (at >= 0 && args.Length >= at + 3)
        {
            new SiteCheckWindow(args[at + 1], args[at + 2]).Show();
            return;
        }
        var selfTest = Array.IndexOf(args, "--check-youtube-selftest");
        if (selfTest >= 0 && args.Length >= selfTest + 2)
        {
            new YouTubeSelfTestWindow(args[selfTest + 1]).Show();
            return;
        }
        var yt = Array.IndexOf(args, "--check-youtube");
        if (yt >= 0 && args.Length >= yt + 3)
        {
            var secondsAt = Array.IndexOf(args, "--seconds");
            var seconds = secondsAt >= 0 && secondsAt + 1 < args.Length && int.TryParse(args[secondsAt + 1], out var s) ? s : 45;
            new YouTubeProbeWindow(args[yt + 1], args[yt + 2], !args.Contains("--no-block"), seconds).Show();
            return;
        }

        // One instance per data folder. After an update the old copy may still be closing, so wait for it.
        var wait = args.Contains(Updater.WaitForPreviousArgument) ? TimeSpan.FromSeconds(20) : TimeSpan.Zero;
        // A newer version launched while an older one is open (a fresh download) closes it and takes over.
        _instance = SingleInstance.TryAcquire(ProfileStore.Root, wait,
            () => InstanceInfo.CloseOlderCopy(ProfileStore.Root, Updater.CurrentVersion));
        if (_instance == null)
        {
            Shutdown();
            return;
        }
        _instance.ListenForSecondLaunch(() => Dispatcher.Invoke(BringToFront));
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    void BringToFront()
    {
        if (MainWindow is not { } w) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();   // the second launch allowed this (AllowSetForegroundWindow), so it really comes to the front
    }

    // An error in a button handler or timer: log it, save the open tabs, and keep going, unless they keep coming.
    void OnUiError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        CrashLog.Write("UI thread", e.Exception);
        if (_handling) return;
        _handling = true;
        try
        {
            (MainWindow as MainWindow)?.SaveSessionSafely();
            var keepGoing = _errors.Record(DateTime.UtcNow);
            if (!keepGoing)
            {
                MessageBox.Show("LightBrowser keeps hitting errors and will close now. Your tabs were saved.\n\nDetails: " + CrashLog.FilePath,
                    "LightBrowser - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            else if (!_told)
            {
                _told = true;
                MessageBox.Show("LightBrowser hit an unexpected error and kept running. Your tabs were saved.\n\n" +
                                "If something looks wrong, restart the app. Details: " + CrashLog.FilePath,
                    "LightBrowser - unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _handling = false; }
    }
}
