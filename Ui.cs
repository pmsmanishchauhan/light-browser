using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LightBrowser;

static class Ui
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Dark title bar on Windows 10 20H1+ / 11. Silently does nothing on older builds.
    public static void DarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            int on = 1;
            DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
        };
    }
}
