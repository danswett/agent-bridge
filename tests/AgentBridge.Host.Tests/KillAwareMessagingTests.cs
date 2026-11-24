using AgentBridge.Host;
using AgentBridge.Host.Lite;
using Xunit;

// After a recorded kill, nothing acts as if own-agents messaging is on unless LiteKill.On says so (docs/design/beta-messaging.md
// section 3; Quality's P2 on #4316): not the peers commands, not the kill's own post-check. An explicit scope never turns it back on.
public sealed class KillAwareMessagingTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-kill-aware-").FullName;
    string Config => Path.Combine(folder, HostSettings.FileName);
    string Record => Path.Combine(folder, LiteKill.FileName);
    public void Dispose() { File.SetAttributes(Config, FileAttributes.Normal); Directory.Delete(folder, true); }

    // What a person left after a kill: the "off" the kill wrote was unset and a scope set instead.
    void AfterAKill(string agentMessaging)
    {
        File.WriteAllText(Config, $$"""{ "version": 1, "agentMessaging": { {{agentMessaging}} } }""");
        File.WriteAllText(Record, """{ "epoch": 1700000000 }""");
    }

    [Theory]
    [InlineData("\"scope\": \"same-person\"", false)]                       // an explicit scope alone never turns it back on
    [InlineData("\"scope\": \"same-person\", \"remote\": \"own-account\"", true)]
    [InlineData("\"remote\": \"own-account\"", true)]
    [InlineData("\"remote\": \"off\"", false)]
    [InlineData("\"scope\": \"off\", \"remote\": \"own-account\"", false)]
    public void After_a_recorded_kill_only_an_explicit_remote_own_account_is_on(string agentMessaging, bool on)
    {
        AfterAKill(agentMessaging);
        Assert.Equal(on, LiteKill.IsOn(folder));
        Assert.Equal(on, new LiteKill(folder).On);   // the reader agrees with the running host's own answer
    }

    [Fact]
    public void Before_any_kill_the_default_and_an_explicit_scope_are_on_and_off_is_off()
    {
        File.WriteAllText(Config, """{ "version": 1, "agentMessaging": { "scope": "same-person" } }""");
        Assert.True(LiteKill.IsOn(folder));
        File.WriteAllText(Config, """{ "version": 1, "agentMessaging": { "remote": "off" } }""");
        Assert.False(LiteKill.IsOn(folder));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "epoch": "soon" }""")]
    [InlineData("""{ "epoch": 1700000000, "held": true }""")]
    public void A_kill_record_that_is_unreadable_or_held_is_off_even_with_an_explicit_remote(string record)
    {
        File.WriteAllText(Config, """{ "version": 1, "agentMessaging": { "remote": "own-account" } }""");
        File.WriteAllText(Record, record);
        Assert.False(LiteKill.IsOn(folder));
    }

    [Fact]
    public void A_kill_record_that_is_not_a_file_is_off()
    {
        File.WriteAllText(Config, """{ "version": 1, "agentMessaging": { "remote": "own-account" } }""");
        Directory.CreateDirectory(Record);
        Assert.False(LiteKill.IsOn(folder));
    }

    [Fact]
    public void The_peers_commands_refuse_after_a_kill_with_an_explicit_scope_and_work_when_it_is_really_on()
    {
        AfterAKill("\"scope\": \"same-person\"");
        var local = new OwnPeerCommands.Local("a".PadLeft(32, 'a'), "account");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(3, OwnPeerCommands.Run(["peers", "show"], folder, local, () => "yes", output, error));
        Assert.Contains("off on this host", error.ToString());
        Assert.Empty(output.ToString());
        using var listed = new StringWriter();
        Assert.Equal(0, OwnPeerCommands.Run(["peers", "list"], folder, local, () => "yes", listed, TextWriter.Null));
        Assert.Contains("is off", listed.ToString());

        AfterAKill("\"scope\": \"same-person\", \"remote\": \"own-account\"");
        using var listedOn = new StringWriter();
        Assert.Equal(0, OwnPeerCommands.Run(["peers", "list"], folder, local, () => "yes", listedOn, TextWriter.Null));
        Assert.DoesNotContain("is off", listedOn.ToString());
    }

    [Fact]
    public async Task The_kill_post_check_asks_the_kill_not_the_setting_and_fails_only_if_messaging_is_really_still_on()
    {
        // The off the kill writes cannot be saved (a read-only config), under a scope: the kill record holds the host off, so the
        // kill worked. The old check read RemoteEnabled, true here, and called the kill failed.
        File.WriteAllText(Config, """{ "version": 1, "agentMessaging": { "scope": "same-person" } }""");
        File.SetAttributes(Config, FileAttributes.ReadOnly);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await AgentCommands.RunAsync(["agents", "lite", "kill"], folder, () => false, output, error));
        Assert.Contains("killed", output.ToString());
        Assert.False(LiteKill.IsOn(folder));
    }
}
