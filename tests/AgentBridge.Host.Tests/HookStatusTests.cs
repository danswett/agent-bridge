using AgentBridge.Hooks;
using AgentBridge.Host;
using AgentBridge.Host.Core.Intake;
using Xunit;

public sealed class HookStatusTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("abhooks-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    string Status(string profile, string runtime)
    {
        var output = new StringWriter();
        Assert.Equal(0, HookCommands.Status(profile, runtime, output, DateTimeOffset.Now));
        return output.ToString();
    }

    [Fact]
    public void A_profile_that_has_not_migrated_says_so_and_never_counts_the_spool()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        var runtime = Directory.CreateDirectory(Path.Combine(_root, "runtime")).FullName;
        Directory.CreateDirectory(SpoolContract.SpoolDirectory(runtime));
        File.WriteAllText(Path.Combine(SpoolContract.SpoolDirectory(runtime), "1.json"), "{}");

        var text = Status(profile, runtime);
        Assert.Contains("hook intake: off", text);
        Assert.Contains("heartbeat: none", text);
        Assert.DoesNotContain("spool:", text);
        Assert.Contains("runs: no runs in the last 24 h", text);
    }

    [Fact]
    public void A_migrated_profile_reports_its_installation_heartbeat_backlog_and_runs()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        var runtime = Directory.CreateDirectory(Path.Combine(_root, "bridge", "runtime")).FullName;
        new IntakeState(runtime, Path.Combine(_root, "bridge"), new string('b', 32), DateTimeOffset.Now).Save(profile);
        File.WriteAllText(SpoolContract.HeartbeatPath(runtime), "123");
        SpoolContract.Write(runtime, "claude", "stop", "{}"u8, [], DateTimeOffset.Now, 1);
        SpoolContract.Record(runtime, "claude", "stop", "spool", null, DateTimeOffset.Now, 31);

        var text = Status(profile, Path.Combine(_root, "elsewhere"));
        Assert.Contains($"hook intake: on, for agent-ha-bridge installation {new string('b', 32)}", text);
        Assert.Contains("heartbeat: fresh", text);
        Assert.Contains("written by pid 123; hooks are spooling", text);
        Assert.Contains("spool: 1 event(s) waiting", text);
        Assert.Contains("runs: 1 runs in the last 24 h, 0 not spooled (0%); median 31 ms", text);
    }
}
