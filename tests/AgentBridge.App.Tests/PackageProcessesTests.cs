using AgentBridge.App.Core;
using Xunit;

public sealed class PackageProcessesTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "profile-refusal", "current");
    static PackageProcesses.Observed Process(int pid, string name, string? args = "serve --profile default") =>
        new(pid, name, Path.Combine(Root, name + ".exe"), args is null ? null : $"\"{Path.Combine(Root, name + ".exe")}\" {args}");

    [Theory]
    [InlineData(null)]
    [InlineData(20)]
    public void The_default_host_and_its_launcher_are_one_profile_even_without_a_connected_host_pid(int? ownHost)
    {
        Assert.Null(PackageProcesses.Others(Root, "default", 10, ownHost,
            [Process(10, "AgentBridge", "--profile default"), Process(20, "agent-bridge-host"), Process(30, "agent-bridge-hostw")]));
    }

    [Theory]
    [InlineData("serve")]
    [InlineData("serve --profile DEFAULT")]
    public void A_same_profile_host_with_implicit_or_case_insensitive_profile_does_not_block(string args)
    {
        Assert.Null(PackageProcesses.Others(Root, "default", 10, null, [Process(20, "agent-bridge-host", args)]));
    }

    [Fact]
    public void Stale_profile_markers_without_running_processes_do_not_block()
    {
        var root = Path.Combine(Path.GetTempPath(), "profile-refusal-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profiles", "work");
        Directory.CreateDirectory(profile);
        try
        {
            File.WriteAllText(Path.Combine(profile, "host.lock"), "4242");
            File.WriteAllText(Path.Combine(profile, "launcher.pid"), "4242 1");

            Assert.Null(PackageProcesses.Others(Path.Combine(root, "current"), "default", 10, null, []));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("agent-bridge-host", "serve --profile work", "host")]
    [InlineData("agent-bridge-hostw", "serve --profile work", "launcher")]
    [InlineData("AgentBridge", "--background --profile work", "app")]
    public void A_second_profile_refuses_with_its_name_and_pid(string name, string args, string kind)
    {
        var why = PackageProcesses.Others(Root, "default", 10, 20, [Process(4242, name, args)]);

        Assert.NotNull(why);
        Assert.Equal($"bridge profile 'work' {kind} (pid 4242) runs from this installation", why);
        Assert.Equal($"The update didn't install because {why}.", AppUpdateViewModel.DidNotInstall(why, true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("serve --profile")]
    [InlineData("serve --profile ../work")]
    [InlineData("serve --profile default --profile work")]
    [InlineData("serve --profile=work")]
    public void An_unreadable_or_ambiguous_profile_is_not_assumed_to_be_default(string? args)
    {
        var why = PackageProcesses.Others(Root, "default", 10, null, [Process(4242, "agent-bridge-host", args)]);

        Assert.Equal("bridge profile unknown host (pid 4242) runs from this installation", why);
    }

    [Fact]
    public void An_unreadable_image_still_refuses_even_when_the_arguments_name_our_profile()
    {
        var observed = Process(4242, "agent-bridge-host") with { Image = null };
        var why = PackageProcesses.Others(Root, "default", 10, null, [observed]);

        Assert.NotNull(why);
        Assert.Equal("bridge profile 'default' host (pid 4242) runs and cannot be looked at", why);
        Assert.Equal($"The update didn't install because {why}.", AppUpdateViewModel.DidNotInstall(why, true));
    }

    [Fact]
    public void A_similarly_named_installation_beside_this_one_is_not_inside_it()
    {
        var observed = Process(4242, "agent-bridge-host", "serve --profile work")
            with { Image = Path.Combine(Root + "-other", "agent-bridge-host.exe") };

        Assert.Null(PackageProcesses.Others(Root, "default", 10, null, [observed]));
    }
}
