using System.Windows;

namespace LightBrowser;

public partial class App : Application
{
    // Normal start opens the main window. "--check-sites <in.json> <out.json>" runs the site checker instead.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var at = Array.IndexOf(args, "--check-sites");
        if (at >= 0 && args.Length >= at + 3)
            new SiteCheckWindow(args[at + 1], args[at + 2]).Show();
        else
            new MainWindow().Show();
    }
}
