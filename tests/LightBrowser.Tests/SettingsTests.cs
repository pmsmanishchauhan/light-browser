using System.IO;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class SettingsTests : DataFolderTest
{
    string SettingsFile => Path.Combine(Dir, "settings.json");

    [Fact]
    public void First_run_defaults_to_Balanced_and_writes_the_file()
    {
        var s = AppSettings.Load();

        Assert.Equal(AppSettings.Balanced, s.Mode);
        Assert.Equal(new AppSettings.ModeLimits(5, 10, 120, true), s.Limits);
        Assert.True(File.Exists(SettingsFile));
    }

    [Theory]
    [InlineData(AppSettings.LowMemory, 2, 3, 60, true)]
    [InlineData(AppSettings.Balanced, 5, 10, 120, true)]
    [InlineData(AppSettings.Performance, 15, 60, 120, false)]
    public void Each_mode_has_its_own_limits(string mode, int max, int idle, int grace, bool trimHidden)
    {
        var s = new AppSettings { Mode = mode };
        Assert.Equal(new AppSettings.ModeLimits(max, idle, grace, trimHidden), s.Limits);
    }

    [Fact]
    public void Modes_are_ordered_from_least_to_most_memory()
    {
        var low = new AppSettings { Mode = AppSettings.LowMemory }.Limits;
        var balanced = new AppSettings { Mode = AppSettings.Balanced }.Limits;
        var fast = new AppSettings { Mode = AppSettings.Performance }.Limits;

        Assert.True(low.MaxLoadedTabs < balanced.MaxLoadedTabs && balanced.MaxLoadedTabs < fast.MaxLoadedTabs);
        Assert.True(low.IdleMinutes < balanced.IdleMinutes && balanced.IdleMinutes < fast.IdleMinutes);
    }

    [Fact]
    public void The_chosen_mode_is_remembered_across_restarts()
    {
        var s = AppSettings.Load();
        s.Mode = AppSettings.LowMemory;
        s.Save();

        Assert.Equal(AppSettings.LowMemory, AppSettings.Load().Mode);
    }

    [Fact]
    public void Custom_mode_uses_the_numbers_in_the_file()
    {
        File.WriteAllText(SettingsFile, "{ \"Mode\": \"Custom\", \"MaxLoadedTabs\": 9, \"IdleMinutes\": 4, \"GraceSeconds\": 7 }");
        Assert.Equal(new AppSettings.ModeLimits(9, 4, 7, true), AppSettings.Load().Limits);
    }

    [Fact]
    public void A_settings_file_from_before_modes_existed_keeps_its_hand_edited_numbers()
    {
        File.WriteAllText(SettingsFile, "{ \"MaxLoadedTabs\": 1, \"IdleMinutes\": 60, \"GraceSeconds\": 0 }");
        var s = AppSettings.Load();

        Assert.Equal(AppSettings.Custom, s.Mode);
        Assert.Equal(1, s.Limits.MaxLoadedTabs);
    }

    [Fact]
    public void An_old_file_with_the_default_numbers_becomes_Balanced()
    {
        File.WriteAllText(SettingsFile, "{ \"MaxLoadedTabs\": 5, \"IdleMinutes\": 10, \"GraceSeconds\": 120 }");
        Assert.Equal(AppSettings.Balanced, AppSettings.Load().Mode);
    }

    [Fact]
    public void An_unknown_mode_or_a_corrupt_file_falls_back_to_safe_defaults()
    {
        File.WriteAllText(SettingsFile, "{ \"Mode\": \"Turbo\" }");
        Assert.Equal(AppSettings.Balanced, AppSettings.Load().Mode);

        File.WriteAllText(SettingsFile, "not json {{{");
        var s = AppSettings.Load();
        Assert.Equal(AppSettings.Balanced, s.Mode);
        Assert.Equal(5, s.Limits.MaxLoadedTabs);
    }

    [Fact]
    public void Out_of_range_custom_numbers_are_clamped()
    {
        File.WriteAllText(SettingsFile, "{ \"Mode\": \"Custom\", \"MaxLoadedTabs\": 0, \"IdleMinutes\": -5, \"GraceSeconds\": 999999 }");
        var limits = AppSettings.Load().Limits;

        Assert.Equal(1, limits.MaxLoadedTabs);
        Assert.Equal(1, limits.IdleMinutes);
        Assert.Equal(3600, limits.GraceSeconds);
    }
}
