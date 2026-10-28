using System.IO;
using System.Text.RegularExpressions;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class VaultTests : DataFolderTest
{
    static VaultEntry Entry(string site, string user, string pw) => new() { Site = site, Username = user, Password = pw };

    string VaultFile(string id) => Path.Combine(Dir, "vault", id + ".vault");

    [Fact]
    public void Entries_roundtrip_and_the_file_never_contains_the_password_in_clear()
    {
        var secret = "Sup3r-Secret-pw!";
        Assert.True(PasswordVault.Save("alice", new[] { Entry("admin.booking.com", "alice@x.com", secret) }));

        var loaded = PasswordVault.Load("alice").Single();
        Assert.Equal("alice@x.com", loaded.Username);
        Assert.Equal(secret, loaded.Password);

        var raw = File.ReadAllText(VaultFile("alice"));
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("alice@x.com", raw);
        Assert.DoesNotContain("booking", raw);
    }

    [Fact]
    public void Each_profile_has_its_own_list()
    {
        PasswordVault.Save("alice", new[] { Entry("booking.com", "alice", "pw-a") });
        PasswordVault.Save("bob", new[] { Entry("booking.com", "bob", "pw-b") });

        Assert.Equal("pw-a", PasswordVault.Load("alice").Single().Password);
        Assert.Equal("pw-b", PasswordVault.Load("bob").Single().Password);
        Assert.Empty(PasswordVault.Load("carol"));
    }

    [Fact]
    public void A_vault_file_copied_onto_another_profile_cannot_be_opened()
    {
        PasswordVault.Save("alice", new[] { Entry("booking.com", "alice", "pw-a") });
        File.Copy(VaultFile("alice"), VaultFile("mallory"));

        Assert.Empty(PasswordVault.Load("mallory"));                          // bound to its profile
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Dir, "vault"), "mallory.vault.corrupt-*"));   // kept aside, not deleted
        Assert.Equal("pw-a", PasswordVault.Load("alice").Single().Password);
    }

    [Fact]
    public void A_damaged_vault_is_kept_aside_and_the_backup_is_used()
    {
        PasswordVault.Save("alice", new[] { Entry("booking.com", "alice", "first") });
        PasswordVault.Save("alice", new[] { Entry("booking.com", "alice", "first"), Entry("expedia.com", "alice", "second") });
        File.WriteAllText(VaultFile("alice"), "not even base64 !!");

        var loaded = PasswordVault.Load("alice");

        Assert.Equal("first", loaded.Single().Password);      // previous version from the .bak
    }

    [Fact]
    public void Delete_removes_the_vault_of_that_profile_only()
    {
        PasswordVault.Save("alice", new[] { Entry("a.com", "a", "1") });
        PasswordVault.Save("bob", new[] { Entry("b.com", "b", "2") });

        PasswordVault.Delete("alice");

        Assert.Empty(PasswordVault.Load("alice"));
        Assert.Single(PasswordVault.Load("bob"));
    }

    [Theory]
    [InlineData("https://www.Admin.Booking.com/hotel?x=1", "admin.booking.com")]
    [InlineData("admin.booking.com", "admin.booking.com")]
    [InlineData("  WWW.Example.com  ", "example.com")]
    [InlineData("", "")]
    [InlineData("not a host", "")]
    public void HostOf_normalises_what_was_typed(string input, string expected) =>
        Assert.Equal(expected, PasswordVault.HostOf(input));

    [Fact]
    public void Entries_for_a_page_match_the_same_host_or_a_related_domain_only()
    {
        var all = new[]
        {
            Entry("booking.com", "parent", "1"), Entry("admin.booking.com", "exact", "2"),
            Entry("expedia.com", "other", "3"), Entry("notbooking.com", "lookalike", "4"),
        };

        var users = PasswordVault.ForHost(all, "https://admin.booking.com/login").Select(e => e.Username).OrderBy(x => x).ToList();

        Assert.Equal(new[] { "exact", "parent" }, users);                 // not "other", and not the look-alike domain
        Assert.Empty(PasswordVault.ForHost(all, "about:blank"));
        Assert.Empty(PasswordVault.ForHost(all, null));
    }

    [Fact]
    public void Generated_passwords_are_long_random_and_use_mixed_characters()
    {
        var a = PasswordVault.Generate();
        var b = PasswordVault.Generate();
        Assert.Equal(18, a.Length);
        Assert.NotEqual(a, b);
        Assert.Equal(40, PasswordVault.Generate(40).Length);
        Assert.Matches("[a-z]", string.Concat(Enumerable.Range(0, 5).Select(_ => PasswordVault.Generate())));
        Assert.Matches("[A-Z]", string.Concat(Enumerable.Range(0, 5).Select(_ => PasswordVault.Generate())));
        Assert.Matches("[0-9]", string.Concat(Enumerable.Range(0, 5).Select(_ => PasswordVault.Generate())));
    }

    [Fact]
    public void The_fill_script_passes_values_as_escaped_literals_so_they_cannot_break_out()
    {
        var evil = "p\"w'\\</script><img src=x onerror=alert(1)>\u2028;alert(2);//";
        var script = PasswordVault.FillScript("us\"er", evil);

        Assert.DoesNotContain("</script>", script);
        Assert.DoesNotContain("<img", script);
        Assert.DoesNotContain("\u2028", script);
        // The password literal is a valid JSON string that decodes back to exactly the original.
        var literal = Regex.Match(script, @", (""(?:[^""\\]|\\.)*"")\)$").Groups[1].Value;
        Assert.Equal(evil, System.Text.Json.JsonSerializer.Deserialize<string>(literal));
    }
}
