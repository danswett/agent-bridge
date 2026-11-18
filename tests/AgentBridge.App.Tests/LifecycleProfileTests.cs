using System.Text.RegularExpressions;
using Xunit;

public sealed class LifecycleProfileTests
{
    static string ProgramSource()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return File.ReadAllText(Path.Combine(root, "src", "AgentBridge.App", "Program.cs"));
    }

    [Fact]
    public void The_profile_is_resolved_before_Velopack_can_run_a_lifecycle_hook()
    {
        var source = ProgramSource();
        var parse = source.IndexOf("var options = AppOptions.Parse(args);", StringComparison.Ordinal);
        var hooks = source.IndexOf("VelopackApp.Build()", StringComparison.Ordinal);

        Assert.True(parse >= 0 && hooks > parse, "Resolve the command-line profile before registering or running Velopack hooks.");
        Assert.Equal(1, Regex.Matches(source, @"AppOptions\.Parse\(args\)").Count);
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("Uninstall")]
    public void Each_lifecycle_callback_passes_the_resolved_profile_to_its_hook(string hook)
    {
        var source = ProgramSource();

        Assert.Contains($".OnBefore{hook}FastCallback(_ => Lifecycle.Before{hook}(options.Profile))", source, StringComparison.Ordinal);
        Assert.Contains($"public static void Before{hook}(string profile)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_hooks_construct_the_manager_for_the_supplied_profile_instead_of_default()
    {
        var source = ProgramSource();

        Assert.Contains("static HostManager Manager(string profile) => App.ManagerFor(profile);", source, StringComparison.Ordinal);
        Assert.Contains("BeforeUpdate(string profile) => StopForHook(\"update\", profile);", source, StringComparison.Ordinal);
        Assert.Contains("Manager(profile).StopWithinAsync(HookProbeWait)", source, StringComparison.Ordinal);
        Assert.Contains("var manager = Manager(profile);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("App.ManagerFor(\"default\")", source, StringComparison.Ordinal);
    }
}
