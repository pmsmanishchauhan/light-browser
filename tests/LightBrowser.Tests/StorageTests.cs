using System.IO;
using System.Text.Json;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

// Every test runs against its own temporary data folder (LIGHTBROWSER_HOME), never the real one.
public abstract class DataFolderTest : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "lightbrowser-tests-" + Guid.NewGuid().ToString("N"));

    protected DataFolderTest()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("LIGHTBROWSER_HOME", Dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LIGHTBROWSER_HOME", null);
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class ProfileStoreTests : DataFolderTest
{
    static Profile Sample(string name, string group = "") =>
        new() { Name = name, Group = group, Notes = "n", Color = "#E5575B", HomeUrls = { "https://booking.com" }, OpenUrls = { "https://a.com/x", "https://b.com/y" } };

    [Fact]
    public void Profiles_roundtrip_with_all_fields()
    {
        var store = new ProfileStore();
        var p = Sample("Hilton Delhi", "Hilton");
        p.LastUsed = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        p.IsDeleted = true;
        store.Profiles.Add(p);
        Assert.True(store.Save());

        var loaded = ProfileStore.Load().Profiles.Single();
        Assert.Equal(p.Id, loaded.Id);
        Assert.Equal("Hilton Delhi", loaded.Name);
        Assert.Equal("Hilton", loaded.Group);
        Assert.Equal("#E5575B", loaded.Color);
        Assert.Equal("n", loaded.Notes);
        Assert.Equal(new[] { "https://booking.com" }, loaded.HomeUrls);
        Assert.Equal(new[] { "https://a.com/x", "https://b.com/y" }, loaded.OpenUrls);
        Assert.Equal(p.LastUsed, loaded.LastUsed);
        Assert.True(loaded.IsDeleted);
    }

    [Fact]
    public void File_from_an_older_version_without_new_fields_still_loads()
    {
        File.WriteAllText(ProfileStore.FilePath, "[{\"Id\":\"abc\",\"Name\":\"Old\",\"Color\":\"#4F8EF7\",\"HomeUrls\":[\"https://x.com\"]}]");
        var p = ProfileStore.Load().Profiles.Single();
        Assert.Equal("Old", p.Name);
        Assert.Equal("", p.Group);
        Assert.False(p.IsDeleted);
        Assert.Empty(p.OpenUrls);
        Assert.Null(p.LastUsed);
    }

    [Fact]
    public void Interrupted_write_leaves_the_previous_file_intact()
    {
        var store = new ProfileStore();
        store.Profiles.Add(Sample("Keep me"));
        store.Save();
        // A write that died half way only ever touched the temp file.
        File.WriteAllText(ProfileStore.FilePath + ".tmp", "[{\"Id\":\"half-writ");

        Assert.Equal("Keep me", ProfileStore.Load().Profiles.Single().Name);
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_the_backup_is_used()
    {
        var store = new ProfileStore();
        store.Profiles.Add(Sample("First"));
        store.Save();
        store.Profiles.Add(Sample("Second"));
        store.Save();                                   // .bak now holds the one-profile version
        File.WriteAllText(ProfileStore.FilePath, "{ not json");

        var loaded = ProfileStore.Load();

        Assert.Equal("First", loaded.Profiles.Single().Name);
        Assert.Contains(Directory.GetFiles(Dir), f => f.Contains("profiles.json.corrupt-"));
    }

    [Fact]
    public void Unreadable_file_and_backup_start_empty_without_deleting_anything()
    {
        File.WriteAllText(ProfileStore.FilePath, "garbage");
        File.WriteAllText(ProfileStore.FilePath + ".bak", "also garbage");

        Assert.Empty(ProfileStore.Load().Profiles);
        Assert.Equal(2, Directory.GetFiles(Dir, "profiles.json*corrupt-*").Length);   // both kept for recovery
    }

    [Fact]
    public void Saving_after_a_corrupt_load_does_not_destroy_the_good_backup()
    {
        var store = new ProfileStore();
        store.Profiles.Add(Sample("Precious"));
        store.Save();
        store.Save();
        File.WriteAllText(ProfileStore.FilePath, "broken");

        var reloaded = ProfileStore.Load();     // recovers "Precious" from the backup
        reloaded.Profiles.Add(Sample("New"));
        reloaded.Save();

        Assert.Equal(2, ProfileStore.Load().Profiles.Count);
    }

    [Fact]
    public void Duplicate_copies_settings_but_not_identity_or_tabs()
    {
        var p = Sample("Client A", "Marriott");
        var copy = p.Duplicate();

        Assert.NotEqual(p.Id, copy.Id);                  // new Id means new, empty, isolated storage
        Assert.NotEqual(p.WebViewProfileName, copy.WebViewProfileName);
        Assert.Equal("Client A (copy)", copy.Name);
        Assert.Equal("Marriott", copy.Group);
        Assert.Equal(p.HomeUrls, copy.HomeUrls);
        Assert.NotSame(p.HomeUrls, copy.HomeUrls);
        Assert.Empty(copy.OpenUrls);
        Assert.Empty(copy.Tabs);
    }

    [Fact]
    public void Tip_combines_notes_and_last_used_and_is_null_when_neither()
    {
        var p = new Profile();
        Assert.Null(p.Tip);
        p.Notes = "Call Raj";
        Assert.Equal("Call Raj", p.Tip);
        p.LastUsed = DateTime.UtcNow;
        Assert.Contains("Last used", p.Tip);
    }

    [Fact]
    public void Each_profile_gets_a_distinct_webview_storage_name()
    {
        var names = Enumerable.Range(0, 500).Select(_ => new Profile().WebViewProfileName).ToHashSet();
        Assert.Equal(500, names.Count);
        Assert.All(names, n => Assert.Matches("^p[0-9a-f]{12}$", n));
    }
}

public class SafeFileTests : DataFolderTest
{
    [Fact]
    public void Write_then_read_returns_the_content()
    {
        var path = Path.Combine(Dir, "x.json");
        SafeFile.Write(path, "[1,2]");
        Assert.Equal(new[] { 1, 2 }, SafeFile.Read(path, j => JsonSerializer.Deserialize<int[]>(j)));
    }

    [Fact]
    public void Missing_file_reads_as_null()
    {
        Assert.Null(SafeFile.Read(Path.Combine(Dir, "nope.json"), j => JsonSerializer.Deserialize<int[]>(j)));
    }
}
