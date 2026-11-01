using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LightBrowser;

// Only one LightBrowser per data folder. Two copies would each rewrite profiles.json, and the last one to save
// would silently overwrite the other's changes. A second launch wakes the first window and exits. (A different
// data folder, set with LIGHTBROWSER_HOME, is a different instance, so test runs do not collide with your own.)
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly EventWaitHandle _wake;
    volatile bool _stop;

    SingleInstance(string id, Mutex mutex)
    {
        _mutex = mutex;
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName(id));
    }

    static string LockName(string id) => @"Local\" + id + ".lock";
    static string WakeName(string id) => @"Local\" + id + ".wake";

    // The same folder always gives the same id (case and trailing slash do not matter); different folders differ.
    public static string IdFor(string dataFolder)
    {
        var normal = Path.GetFullPath(dataFolder).TrimEnd('\\', '/').ToLowerInvariant();
        return "LightBrowser." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal)), 0, 8);
    }

    // Takes the lock, or returns null if another instance holds it (after asking that one to come to the front).
    // "wait" gives a previous instance that is still shutting down (an update restart) time to let go.
    public static SingleInstance? TryAcquire(string dataFolder, TimeSpan wait)
    {
        var id = IdFor(dataFolder);
        var mutex = new Mutex(false, LockName(id));
        bool got;
        try { got = mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { got = true; }   // the last instance crashed; the lock is ours
        if (!got)
        {
            mutex.Dispose();
            Wake(id);
            return null;
        }
        return new SingleInstance(id, mutex);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static void Wake(string id)
    {
        // This process was just started by the user, so Windows lets it hand the foreground to the first instance.
        try { AllowSetForegroundWindow(-1); } catch (Exception) { }
        try { using var w = EventWaitHandle.OpenExisting(WakeName(id)); w.Set(); }
        catch (Exception) { }   // the first instance is not listening yet or just left
    }

    // Runs onWake (on a background thread) each time another launch asks this instance to come forward.
    public void ListenForSecondLaunch(Action onWake)
    {
        var thread = new Thread(() =>
        {
            while (!_stop)
                if (_wake.WaitOne(500) && !_stop) onWake();
        }) { IsBackground = true, Name = "LightBrowser second-launch listener" };
        thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        try { _mutex.ReleaseMutex(); } catch (Exception) { }
        _mutex.Dispose();
        _wake.Dispose();
    }
}
