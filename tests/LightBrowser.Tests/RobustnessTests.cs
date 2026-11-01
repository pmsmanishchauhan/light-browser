using System.Diagnostics;
using System.IO;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class RobustnessTests : DataFolderTest
{
    // ---- dialogs fit the screen ----

    [Theory]
    [InlineData(620, 1040, null, 620)]      // plenty of room: keep the preferred height
    [InlineData(620, 728, null, 620)]       // a 768-high screen minus the taskbar still fits 620
    [InlineData(620, 600, null, 560)]       // a short screen: cut to the work area minus the margin
    [InlineData(620, 1040, 480.0, 480)]     // a test can cap it further
    [InlineData(620, 300, null, 320)]       // never below the smallest usable height
    [InlineData(double.MaxValue, 900, null, 860)]   // "as tall as it likes" means the biggest that fits
    public void A_dialog_is_never_taller_than_the_screen_allows(double preferred, double workArea, double? cap, double expected) =>
        Assert.Equal(expected, DialogSizing.Fit(preferred, workArea, cap));

    // ---- one instance per data folder ----

    [Fact]
    public void The_same_folder_gives_the_same_instance_id_and_other_folders_differ()
    {
        var a = SingleInstance.IdFor(@"C:\Data\LightBrowser");
        Assert.Equal(a, SingleInstance.IdFor(@"c:\data\lightbrowser\"));
        Assert.Equal(a, SingleInstance.IdFor(@"C:\Data\LightBrowser/"));
        Assert.NotEqual(a, SingleInstance.IdFor(@"C:\Data\Other"));
        Assert.StartsWith("LightBrowser.", a);
    }

    static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        var t = new Thread(() => result = work());
        t.Start();
        t.Join();
        return result;
    }

    [Fact]
    public void A_second_launch_is_refused_wakes_the_first_and_a_new_one_can_start_after_it_closes()
    {
        var folder = Path.Combine(Dir, "instance-test");
        using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
        Assert.NotNull(first);

        var woken = new ManualResetEventSlim();
        first!.ListenForSecondLaunch(woken.Set);

        Assert.Null(OnOtherThread(() => SingleInstance.TryAcquire(folder, TimeSpan.Zero)));
        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)), "the first instance was not asked to come forward");

        // A different data folder is a different instance.
        var other = OnOtherThread(() => { var i = SingleInstance.TryAcquire(Path.Combine(Dir, "another"), TimeSpan.Zero); i?.Dispose(); return i != null; });
        Assert.True(other);
    }

    [Fact]
    public void After_the_first_instance_closes_the_lock_is_free_again()
    {
        var folder = Path.Combine(Dir, "instance-test2");
        bool again = false;
        var owner = new Thread(() =>
        {
            using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
            Thread.Sleep(100);
        });
        owner.Start();
        owner.Join();

        var t = new Thread(() => { var i = SingleInstance.TryAcquire(folder, TimeSpan.Zero); again = i != null; i?.Dispose(); });
        t.Start();
        t.Join();
        Assert.True(again);
    }

    [Fact]
    public void A_restart_waits_for_the_old_copy_to_let_go()
    {
        var folder = Path.Combine(Dir, "instance-test3");
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
            held.Set();
            release.Wait();
        });
        owner.Start();
        held.Wait();

        var sw = Stopwatch.StartNew();
        var acquired = false;
        var waiter = new Thread(() =>
        {
            var i = SingleInstance.TryAcquire(folder, TimeSpan.FromSeconds(10));
            acquired = i != null;
            i?.Dispose();
        });
        waiter.Start();
        Thread.Sleep(300);
        release.Set();
        owner.Join();
        waiter.Join();

        Assert.True(acquired);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void Without_waiting_a_busy_lock_is_reported_straight_away()
    {
        var folder = Path.Combine(Dir, "instance-test4");
        using var first = SingleInstance.TryAcquire(folder, TimeSpan.Zero);
        var sw = Stopwatch.StartNew();

        Assert.Null(OnOtherThread(() => SingleInstance.TryAcquire(folder, TimeSpan.Zero)));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    // ---- crash log ----

    [Fact]
    public void An_error_is_written_with_when_where_and_the_details()
    {
        try { throw new InvalidOperationException("boom 123"); }
        catch (Exception ex) { CrashLog.Write("UI thread", ex); }

        var text = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("UI thread", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom 123", text);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]", text);
    }

    [Fact]
    public void Errors_are_appended_and_the_log_stays_small()
    {
        CrashLog.Write("one", new Exception("first"));
        CrashLog.Write("two", new Exception("second"));
        var both = File.ReadAllText(CrashLog.FilePath);
        Assert.True(both.IndexOf("first") < both.IndexOf("second"));

        File.WriteAllText(CrashLog.FilePath, new string('x', (int)CrashLog.MaxBytes + 10));
        CrashLog.Write("three", new Exception("third"));

        Assert.True(File.Exists(CrashLog.FilePath + ".old"));
        var fresh = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("third", fresh);
        Assert.True(fresh.Length < 5000);
    }

    [Fact]
    public void Writing_the_log_never_throws_even_when_it_cannot_be_written()
    {
        Directory.CreateDirectory(CrashLog.FilePath);   // a folder where the file should be

        CrashLog.Write("cannot write", new Exception("x"));
        CrashLog.Write("null error", null);
    }

    // ---- error budget ----

    [Fact]
    public void A_few_errors_are_tolerated_but_a_burst_is_not()
    {
        var budget = new ErrorBudget(8, TimeSpan.FromSeconds(60));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 7; i++) Assert.True(budget.Record(now.AddSeconds(i)));
        Assert.False(budget.Record(now.AddSeconds(8)));
    }

    [Fact]
    public void Old_errors_age_out_of_the_budget()
    {
        var budget = new ErrorBudget(3, TimeSpan.FromSeconds(60));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(budget.Record(now));
        Assert.True(budget.Record(now.AddSeconds(10)));
        Assert.True(budget.Record(now.AddSeconds(200)));      // the first two are over a minute old now
        Assert.True(budget.Record(now.AddSeconds(210)));
        Assert.False(budget.Record(now.AddSeconds(211)));     // three within a minute
    }

    // ---- restart ----

    [Fact]
    public void The_restarted_app_is_told_to_wait_for_the_old_one()
    {
        Assert.Equal("--wait-for-previous", Updater.WaitForPreviousArgument);
    }
}
