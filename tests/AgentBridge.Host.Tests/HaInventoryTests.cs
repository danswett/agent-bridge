using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class HaInventoryTests
{
    static readonly string[] Artifacts = ["ha-token.dpapi", "ha-token", "ha-auth.json", "ha-connector.json"];

    static (int Code, string Text, string Error) Inventory(string folder)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = HaCommands.TryInventoryCommand(["ha", "--profile", Path.GetFileName(folder), "inventory"],
            Path.GetDirectoryName(folder)!, output, error);
        return (code ?? throw new InvalidOperationException("Inventory was not dispatched"), output.ToString(), error.ToString());
    }

    [Fact]
    public void Inventory_dispatch_reports_an_absent_profile_without_creating_an_identity()
    {
        using var fixture = new Fixture();
        var folder = Path.Combine(fixture.Root, "absent");
        var (code, text, error) = Inventory(folder);

        Assert.Equal(0, code);
        Assert.Empty(error);
        foreach (var name in Artifacts) { Assert.Contains($"{name}: absent", text); }
        Assert.Contains("file presence only", text);
        Assert.False(Directory.Exists(folder));
        Assert.False(HaCommands.Writes(["ha", "inventory"]));
    }

    [Fact]
    public void Present_artifacts_are_not_opened_decrypted_printed_or_changed_even_when_corrupt()
    {
        using var fixture = new Fixture();
        var files = Artifacts.Concat(["config.json", "intake.json", "key.json", "account.json"])
            .ToDictionary(name => name, name => System.Text.Encoding.UTF8.GetBytes("private-fixture-content-" + name));
        var locks = new List<FileStream>();
        try
        {
            foreach (var (name, bytes) in files)
            {
                var path = Path.Combine(fixture.Root, name);
                File.WriteAllBytes(path, bytes);
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
            }
            var (code, text, error) = Inventory(fixture.Root);

            Assert.Equal(0, code);
            Assert.Empty(error);
            foreach (var name in Artifacts) { Assert.Contains($"{name}: present", text); }
            Assert.DoesNotContain("private-fixture-content", text + error);
            Assert.DoesNotContain("intake.json", text + error);
            Assert.DoesNotContain("key.json", text + error);
            Assert.DoesNotContain("account.json", text + error);
            Assert.Contains("not checked", text);
        }
        finally { foreach (var file in locks) { file.Dispose(); } }

        Assert.Equal(files.Keys.Order(), Directory.GetFiles(fixture.Root).Select(Path.GetFileName).Order());
        foreach (var (name, bytes) in files) { Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Root, name))); }
    }

    [Fact]
    public void A_directory_in_place_of_a_store_is_unknown_not_absent_or_a_usable_credential()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "ha-token.dpapi"));

        var (code, text, error) = Inventory(fixture.Root);

        Assert.Equal(1, code);
        Assert.Contains("ha-token.dpapi: unknown", error);
        Assert.DoesNotContain("ha-token.dpapi: absent", text);
        Assert.Contains("ha-auth.json: absent", text);
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "ha-token.dpapi")));
    }

    [Fact]
    public void Each_profile_is_inventoried_independently_on_repeated_runs()
    {
        using var fixture = new Fixture();
        var first = Directory.CreateDirectory(Path.Combine(fixture.Root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(fixture.Root, "second")).FullName;
        File.WriteAllText(Path.Combine(second, "ha-token"), "private-fixture-content");

        Assert.Contains("ha-token: absent", Inventory(first).Text);
        var before = Inventory(second);
        Assert.Equal(0, before.Code);
        Assert.Contains("ha-token: present", before.Text);
        Assert.Equal(before, Inventory(second));
        Assert.Empty(Directory.GetFiles(first));
        Assert.Equal("private-fixture-content", File.ReadAllText(Path.Combine(second, "ha-token")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\outside")]
    public void Inventory_dispatch_refuses_a_profile_path_without_reading_or_creating_it(string profile)
    {
        using var fixture = new Fixture();
        var error = new StringWriter();
        Assert.Equal(2, HaCommands.TryInventoryCommand(["ha", "inventory", "--profile", profile],
            fixture.Root, new StringWriter(), error));
        Assert.Contains("profile name", error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Fact]
    public void Inventory_dispatch_never_dispatches_credential_writes()
    {
        using var fixture = new Fixture();
        var error = new StringWriter();
        Assert.Null(HaCommands.TryInventoryCommand(["ha", "token", "clear"], fixture.Root, new StringWriter(), error));
        Assert.Empty(error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }
}
