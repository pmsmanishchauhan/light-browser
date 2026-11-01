using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LightBrowser;

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
