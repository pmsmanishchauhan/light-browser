using System.IO;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class TakeoverTests : DataFolderTest
{
    // ---- a newer launch replaces the open older copy ----

    [Theory]
    [InlineData("0.4.2", "0.4.1", true)]
    [InlineData("0.5.0", "0.4.9", true)]
    [InlineData("0.4.1", "0.4.1", false)]   // the same version just wakes the open one
    [InlineData("0.4.0", "0.4.1", false)]   // an old download never replaces a newer app
    public void Only_a_strictly_newer_launch_replaces_the_running_copy(string mine, string running, bool replace) =>
        Assert.Equal(replace, InstanceInfo.ShouldReplace(Version.Parse(mine), Version.Parse(running)));

    [Fact]
    public void An_unknown_running_version_is_never_replaced() => Assert.False(InstanceInfo.ShouldReplace(new Version(9, 0, 0), null));

    [Fact]
    public void The_note_about_the_running_copy_round_trips_even_with_a_pipe_in_the_path()
    {
        var text = InstanceInfo.Format(1234, new Version(0, 4, 2), @"C:\Apps\Light|Browser.exe");
        var back = InstanceInfo.Parse(text);

        Assert.NotNull(back);
        Assert.Equal(1234, back!.Pid);
        Assert.Equal(new Version(0, 4, 2), back.Version);
        Assert.Equal(@"C:\Apps\Light|Browser.exe", back.Exe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("abc|0.4.2|C:\\x.exe")]
    [InlineData("0|0.4.2|C:\\x.exe")]
    [InlineData("-5|0.4.2|C:\\x.exe")]
    public void A_damaged_note_is_ignored(string? text) => Assert.Null(InstanceInfo.Parse(text));

    [Fact]
    public void A_running_copy_leaves_a_note_and_removes_it_when_it_closes()
    {
        var folder = Path.Combine(Dir, "note");
        var note = Path.Combine(folder, "instance.txt");
        using (var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero))
        {
            Assert.NotNull(first);
            var info = InstanceInfo.Parse(File.ReadAllText(note));
            Assert.Equal(Environment.ProcessId, info!.Pid);
            Assert.Equal(Updater.CurrentVersion, info.Version);
        }
        Assert.False(File.Exists(note));
    }

    [Fact]
    public void A_stale_note_from_a_dead_process_closes_nothing()
    {
        var folder = Path.Combine(Dir, "stale");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "instance.txt"), InstanceInfo.Format(int.MaxValue - 1, new Version(0, 0, 1), @"C:\gone\LightBrowser.exe"));

        Assert.False(InstanceInfo.CloseOlderCopy(folder, new Version(9, 9, 9)));
    }

    [Fact]
    public void A_note_that_names_another_program_closes_nothing()
    {
        // This test process is running, but it is not the exe the note names (pid reuse after a crash).
        var folder = Path.Combine(Dir, "reused");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "instance.txt"), InstanceInfo.Format(Environment.ProcessId, new Version(0, 0, 1), @"C:\elsewhere\LightBrowser.exe"));

        Assert.False(InstanceInfo.CloseOlderCopy(folder, new Version(9, 9, 9)));
    }

    [Fact]
    public void A_newer_launch_that_asked_the_old_copy_to_close_waits_and_then_takes_over()
    {
        var folder = Path.Combine(Dir, "takeover");
        var held = new ManualResetEventSlim();
        var close = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
            held.Set();
            close.Wait();   // "closing" the old copy
        });
        owner.Start();
        held.Wait();

        SingleInstance? taken = null;
        var asked = false;
        var newer = new Thread(() =>
        {
            taken = SingleInstance.TryAcquire(folder, TimeSpan.Zero, () => { asked = true; close.Set(); return true; });
        });
        newer.Start();
        newer.Join();
        owner.Join();

        Assert.True(asked);
        Assert.NotNull(taken);
        taken!.Dispose();
    }

    [Fact]
    public void When_the_launch_is_not_newer_the_running_copy_is_left_alone_and_woken()
    {
        var folder = Path.Combine(Dir, "notnewer");
        using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
        var woken = new ManualResetEventSlim();
        first!.ListenForSecondLaunch(woken.Set);

        SingleInstance? second = null;
        var t = new Thread(() => second = SingleInstance.TryAcquire(folder, TimeSpan.Zero, () => false));
        t.Start();
        t.Join();

        Assert.Null(second);
        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void If_asking_the_old_copy_to_close_fails_the_newer_launch_gives_up_and_wakes_it()
    {
        var folder = Path.Combine(Dir, "stuck");
        using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);

        SingleInstance? second = null;
        var t = new Thread(() => second = SingleInstance.TryAcquire(folder, TimeSpan.Zero, () => throw new InvalidOperationException("no")));
        t.Start();
        t.Join();

        Assert.Null(second);
    }

    // ---- desktop shortcut ----

    const string Exe = @"C:\Apps\LightBrowser.exe";

    [Theory]
    [InlineData(false, false, null, false, DesktopShortcut.Plan.Create)]   // first run: make it
    [InlineData(true, false, null, false, DesktopShortcut.Plan.None)]      // deleted on purpose: leave it deleted
    [InlineData(false, true, Exe, true, DesktopShortcut.Plan.None)]        // already right
    [InlineData(true, true, Exe, true, DesktopShortcut.Plan.None)]
    [InlineData(true, true, @"C:\Old\LightBrowser.exe", false, DesktopShortcut.Plan.Repair)]    // the exe moved: mend it
    [InlineData(true, true, @"C:\Other\LightBrowser.exe", true, DesktopShortcut.Plan.None)]     // points at another copy that exists
    [InlineData(true, true, null, false, DesktopShortcut.Plan.Repair)]                           // unreadable and nothing there
    public void The_shortcut_is_created_once_and_mended_only_when_broken(bool offered, bool exists, string? target, bool targetExists, DesktopShortcut.Plan expected) =>
        Assert.Equal(expected, DesktopShortcut.Decide(offered, exists, target, targetExists, Exe));

    [Fact]
    public void The_shortcut_is_created_on_a_desktop_folder_and_points_at_the_exe()
    {
        var desktop = Path.Combine(Dir, "desktop");
        Directory.CreateDirectory(desktop);
        var exe = Path.Combine(Dir, "app", "LightBrowser.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");

        var first = DesktopShortcut.Ensure(false, exe, desktop);
        var link = Path.Combine(desktop, DesktopShortcut.FileName);

        Assert.Equal(DesktopShortcut.Outcome.Created, first);
        Assert.True(File.Exists(link));
        Assert.Equal(exe, DesktopShortcut.ReadTarget(link), ignoreCase: true);
        Assert.Equal(DesktopShortcut.Outcome.Present, DesktopShortcut.Ensure(true, exe, desktop));

        // Deleting it sticks.
        File.Delete(link);
        Assert.Equal(DesktopShortcut.Outcome.Nothing, DesktopShortcut.Ensure(true, exe, desktop));
        Assert.False(File.Exists(link));
    }

    [Fact]
    public void A_shortcut_to_an_exe_that_was_moved_is_repaired()
    {
        var desktop = Path.Combine(Dir, "desktop2");
        Directory.CreateDirectory(desktop);
        var oldExe = Path.Combine(Dir, "old", "LightBrowser.exe");
        var newExe = Path.Combine(Dir, "new", "LightBrowser.exe");
        foreach (var e in new[] { oldExe, newExe }) { Directory.CreateDirectory(Path.GetDirectoryName(e)!); File.WriteAllText(e, "x"); }
        DesktopShortcut.Ensure(false, oldExe, desktop);
        File.Delete(oldExe);

        Assert.Equal(DesktopShortcut.Outcome.Repaired, DesktopShortcut.Ensure(true, newExe, desktop));
        Assert.Equal(newExe, DesktopShortcut.ReadTarget(Path.Combine(desktop, DesktopShortcut.FileName)), ignoreCase: true);
    }

    [Fact]
    public void Without_a_desktop_folder_nothing_happens_and_nothing_throws()
    {
        Assert.Equal(DesktopShortcut.Outcome.Nothing, DesktopShortcut.Ensure(false, @"C:\x\LightBrowser.exe", null));
        Assert.Equal(DesktopShortcut.Outcome.Nothing, DesktopShortcut.Ensure(false, @"C:\x\LightBrowser.exe", Path.Combine(Dir, "no-such-folder")));
        Assert.Equal(DesktopShortcut.Outcome.Nothing, DesktopShortcut.Ensure(false, null, Dir));
    }

    [Fact]
    public void The_new_setting_defaults_to_not_offered() => Assert.False(new AppSettings().DesktopShortcutOffered);
}
