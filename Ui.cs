using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LightBrowser;

// How tall a dialog may be so its buttons stay on screen, whatever the screen.
public static class DialogSizing
{
    public const double Margin = 40;      // room for the taskbar and window frame
    public const double Smallest = 320;

    // The preferred height, cut down to the work area (minus a margin) and to an optional cap. Never below Smallest.
    public static double Fit(double preferred, double workAreaHeight, double? cap = null)
    {
        var limit = workAreaHeight - Margin;
        if (cap is { } c) limit = Math.Min(limit, c);
        return Math.Max(Smallest, Math.Min(preferred, limit));
    }
}

static class Ui
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static System.Windows.Media.ImageSource? _icon;

    // The app icon, shared by the main window and every dialog.
    static System.Windows.Media.ImageSource? AppIcon()
    {
        try { return _icon ??= System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/assets/icon.ico")); }
        catch (Exception) { return null; }
    }

    // LIGHTBROWSER_MAX_DIALOG_HEIGHT lets a test pretend the screen is small.
    static double? TestCap =>
        double.TryParse(Environment.GetEnvironmentVariable("LIGHTBROWSER_MAX_DIALOG_HEIGHT"), out var h) ? h : null;

    // A dialog's height: what it wants, but never more than fits on this screen.
    public static double FitHeight(double preferred) => DialogSizing.Fit(preferred, SystemParameters.WorkArea.Height, TestCap);

    // The most a dialog that sizes itself to its content may grow to.
    public static double MaxDialogHeight => FitHeight(double.MaxValue);

    // Dark title bar on Windows 10 20H1+ / 11. Silently does nothing on older builds. Also gives dialogs the app icon.
    public static void DarkTitleBar(Window window)
    {
        window.Icon ??= AppIcon();
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            int on = 1;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
        };
    }
}
