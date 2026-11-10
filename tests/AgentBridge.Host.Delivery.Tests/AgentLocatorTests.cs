using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Delivery.Tests;

// These change the process PATH (and, for one, the working directory), so they never run
// beside another test.
[CollectionDefinition(nameof(AgentLocatorTests), DisableParallelization = true)]
public sealed class AgentLocatorCollection;

[Collection(nameof(AgentLocatorTests))]
public sealed class AgentLocatorTests : IDisposable
{
    // A name no machine has installed, so the real PATH cannot answer for it.
    static readonly Launcher Agent = new("zz", "Zz", AgentKind.Copilot, "zz-locator-test-agent", "copilot");
    static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    readonly string root = Directory.CreateTempSubdirectory("agent-locator-").FullName;
    readonly string? savedPath = Environment.GetEnvironmentVariable("PATH");
    readonly string savedDirectory = Directory.GetCurrentDirectory();
    readonly FakeTimeProvider time = new();

    public void Dispose()
    {
        Directory.SetCurrentDirectory(savedDirectory);
        Environment.SetEnvironmentVariable("PATH", savedPath);
        Directory.Delete(root, recursive: true);
    }

    string Dir(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    static string Touch(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, "");
        return path;
    }

    static void SetPath(params string[] dirs) => Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, dirs));

    string? Find(AgentLocator locator, AgentConfig? config = null) => locator.Find(Agent, config ?? AgentConfig.Default);

    [Fact]
    public void A_configured_path_that_is_missing_gives_null_and_never_searches_PATH()
    {
        var onPath = Dir("bin");
        Touch(onPath, Agent.Command + Exe);
        SetPath(onPath);

        Assert.Null(Find(new AgentLocator(time), new AgentConfig { Path = Path.Combine(root, "nowhere", "zz" + Exe) }));
    }

    [Fact]
    public void A_configured_path_that_exists_gives_its_full_path()
    {
        var file = Touch(Dir("own"), "custom" + Exe);
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);

        Assert.Equal(file, Find(new AgentLocator(time), new AgentConfig { Path = relative }));
    }

    [Fact]
    public void A_configured_file_is_runnable_by_extension_on_Windows_and_any_file_elsewhere()
    {
        var script = Touch(Dir("own"), "custom.ps1");

        Assert.Equal(OperatingSystem.IsWindows() ? null : script, Find(new AgentLocator(time), new AgentConfig { Path = script }));
    }

    [Fact]
    public void The_first_PATH_directory_that_has_the_agent_wins()
    {
        var first = Dir("first");
        var second = Dir("second");
        var expected = Touch(first, Agent.Command + Exe);
        Touch(second, Agent.Command + Exe);
        SetPath(first, second);

        Assert.Equal(expected, Find(new AgentLocator(time)));
    }

    [Fact]
    public void Nothing_is_found_when_no_PATH_directory_has_the_agent()
    {
        SetPath(Dir("empty"));

        Assert.Null(Find(new AgentLocator(time)));
    }

    [Fact]
    public void On_Windows_exe_beats_cmd_beats_bat_and_a_ps1_shim_is_ignored()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var dir = Dir("bin");
        SetPath(dir);
        var locator = new AgentLocator(time);
        Touch(dir, Agent.Command + ".ps1");
        Assert.Null(Find(locator));

        var bat = Touch(dir, Agent.Command + ".bat");
        locator.Refresh();
        Assert.Equal(bat, Find(locator));

        var cmd = Touch(dir, Agent.Command + ".cmd");
        Assert.Equal(cmd, Find(locator));

        var exe = Touch(dir, Agent.Command + ".exe");
        Assert.Equal(exe, Find(locator));
    }

    [Fact]
    public void Off_Windows_only_the_extensionless_file_is_found()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var dir = Dir("bin");
        Touch(dir, Agent.Command + ".sh");
        SetPath(dir);
        Assert.Null(Find(new AgentLocator(time)));

        var plain = Touch(dir, Agent.Command);
        Assert.Equal(plain, Find(new AgentLocator(time)));
    }

    [Fact]
    public void Relative_PATH_entries_are_skipped_and_a_repeated_directory_is_read_once()
    {
        var bin = Dir("bin");
        var other = Dir("other");
        SetPath("relative-bin", bin, bin, other);

        var kept = new AgentLocator(time).ChildPath().Split(Path.PathSeparator);

        Assert.DoesNotContain("relative-bin", kept);
        Assert.Single(kept, e => e == bin);
        Assert.Equal(other, kept[Array.IndexOf(kept, bin) + 1]);
    }

    [Fact]
    public void A_relative_PATH_entry_is_never_searched()
    {
        Touch(Dir("here"), Agent.Command + Exe);
        Directory.SetCurrentDirectory(root);
        SetPath("here");

        Assert.Null(Find(new AgentLocator(time)));
    }

    [Fact]
    public void A_PATH_change_is_not_seen_inside_one_minute_but_is_after_it()
    {
        var one = Dir("one");
        var two = Dir("two");
        var inTwo = Touch(two, Agent.Command + Exe);
        SetPath(one);
        var locator = new AgentLocator(time);
        Assert.Null(Find(locator));

        SetPath(one, two);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(Find(locator));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(inTwo, Find(locator));
    }

    [Fact]
    public void Refresh_makes_a_PATH_change_visible_at_once()
    {
        var one = Dir("one");
        var two = Dir("two");
        var inTwo = Touch(two, Agent.Command + Exe);
        SetPath(one);
        var locator = new AgentLocator(time);
        Assert.Null(Find(locator));

        SetPath(one, two);
        locator.Refresh();

        Assert.Equal(inTwo, Find(locator));
    }
}
