using System.Text.Json;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

// C3 PR2 (docs/design/beta-messaging.md section 3): agentMessaging.scope, "same-person" or "off". The old agentMessaging.remote is
// still read; off from either key wins; every consumer keeps asking RemoteEnabled, so the scope changes who may message, never how.
public sealed class MessagingScopeTests
{
    static (AgentMessagingSettings Settings, IReadOnlyList<string> Problems) Read(string? json)
    {
        if (json is null) { return AgentMessagingSettings.Read(null); }
        using var doc = JsonDocument.Parse(json);
        return AgentMessagingSettings.Read(doc.RootElement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"scope":"same-person"}""")]
    [InlineData("""{"remote":"own-account"}""")]
    [InlineData("""{"remote":"own-account","scope":"same-person"}""")]
    public void The_default_and_every_spelling_of_on_is_the_same_person_scope(string? json)
    {
        var (s, problems) = Read(json);
        Assert.True(s.RemoteEnabled);
        Assert.Equal(AgentMessagingSettings.SamePerson, s.Scope);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("""{"scope":"off"}""")]
    [InlineData("""{"remote":"off"}""")]
    [InlineData("""{"remote":"off","scope":"same-person"}""")]
    [InlineData("""{"remote":"own-account","scope":"off"}""")]
    public void Off_from_either_key_wins(string json)
    {
        var (s, _) = Read(json);
        Assert.False(s.RemoteEnabled);
        Assert.Equal(AgentMessagingSettings.Off, s.Scope);
    }

    [Theory]
    [InlineData("""{"scope":"everyone"}""")]
    [InlineData("""{"scope":"other-person"}""")]
    [InlineData("""{"scope":true}""")]
    [InlineData("""{"scope":7}""")]
    [InlineData("""{"scope":"Same-Person"}""")]
    [InlineData("""{"remote":"own-account","scope":"everyone"}""")]
    public void An_unknown_scope_is_off_and_is_reported_and_never_widens(string json)
    {
        var (s, problems) = Read(json);
        Assert.False(s.RemoteEnabled);
        Assert.Equal(AgentMessagingSettings.Off, s.Scope);
        Assert.Contains(problems, p => p.Contains("agentMessaging.scope"));
    }

    [Fact]
    public void A_null_scope_is_the_default_and_a_section_that_is_not_an_object_is_off()
    {
        Assert.True(Read("""{"scope":null}""").Settings.RemoteEnabled);
        using var doc = JsonDocument.Parse("[]");
        var (s, problems) = AgentMessagingSettings.Read(doc.RootElement);
        Assert.False(s.RemoteEnabled);
        Assert.Equal(AgentMessagingSettings.Off, s.Scope);
        Assert.NotEmpty(problems);
        Assert.Equal(AgentMessagingSettings.Off, AgentMessagingSettings.Closed.Scope);
    }

    // The flags that need an explicit "on" count it from the scope key too, and an explicit off from either key keeps them off.
    [Fact]
    public void An_explicit_same_person_scope_is_an_explicit_on_for_the_flags_that_need_one()
    {
        Assert.True(Read("""{"scope":"same-person","laneDelivery":true}""").Settings.LaneDelivery);
        Assert.True(Read("""{"scope":"same-person","allowAllTargets":true}""").Settings.AllowAllTargets);
        Assert.False(Read("""{"laneDelivery":true}""").Settings.LaneDelivery);
        Assert.False(Read("""{"scope":"off","laneDelivery":true}""").Settings.LaneDelivery);
        Assert.False(Read("""{"remote":"own-account","scope":"off","allowAllTargets":true}""").Settings.AllowAllTargets);
        Assert.True(Read("""{"scope":"same-person"}""").Settings.RemoteSet);
    }

    // A person sets it at this machine with config set (which Program.cs refuses inside an agent session, #67); a kill and an
    // unreadable file leave it off, and the new value holds from the next read with no restart.
    [Fact]
    public void A_person_can_set_the_scope_with_config_and_a_kill_or_unreadable_file_leaves_it_off()
    {
        var root = Path.Combine(Path.GetTempPath(), "scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.scope", "same-person"], root, TextWriter.Null, TextWriter.Null));
            Assert.Equal(AgentMessagingSettings.SamePerson, HostSettings.CurrentAgentMessaging(root).Scope);
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.scope", "off"], root, TextWriter.Null, TextWriter.Null));
            Assert.Equal(AgentMessagingSettings.Off, HostSettings.CurrentAgentMessaging(root).Scope);
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.scope", "same-person"], root, TextWriter.Null, TextWriter.Null));
            new LiteKill(root).Kill();
            Assert.Equal(AgentMessagingSettings.Off, HostSettings.CurrentAgentMessaging(root).Scope);
            File.WriteAllText(Path.Combine(root, HostSettings.FileName), "{ not json");
            Assert.Equal(AgentMessagingSettings.Off, HostSettings.CurrentAgentMessaging(root).Scope);
        }
        finally { Directory.Delete(root, true); }
    }

    // docs/design/beta-messaging.md section 3: a kill writes remote "off"; the documented re-enable is remote own-account, and a scope
    // of same-person alone does not undo it (off from either key wins).
    [Fact]
    public void After_a_kill_only_an_explicit_remote_own_account_turns_messaging_back_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "scope-kill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], root, TextWriter.Null, TextWriter.Null));
            new LiteKill(root).Kill();
            Assert.False(HostSettings.CurrentAgentMessaging(root).RemoteEnabled);
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.scope", "same-person"], root, TextWriter.Null, TextWriter.Null));
            Assert.Equal(AgentMessagingSettings.Off, HostSettings.CurrentAgentMessaging(root).Scope);
            Assert.False(new LiteKill(root).On);
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], root, TextWriter.Null, TextWriter.Null));
            Assert.Equal(AgentMessagingSettings.SamePerson, HostSettings.CurrentAgentMessaging(root).Scope);
        }
        finally { Directory.Delete(root, true); }
    }

    // The ONE rule (docs/design/beta-messaging.md section 3): once a kill is recorded, only an explicit remote "own-account" turns messaging
    // back on. A scope of same-person never does, and neither does unsetting the remote key a kill wrote (receipt: #4316 comment 7124772690).
    [Fact]
    public void After_a_kill_a_scope_of_same_person_with_the_remote_key_unset_does_not_turn_messaging_back_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "scope-unset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.scope", "same-person"], root, TextWriter.Null, TextWriter.Null));
            var kill = new LiteKill(root);
            kill.Kill();
            Assert.False(kill.On);
            Assert.Equal(0, ConfigCommands.Run(["config", "unset", "agentMessaging.remote"], root, TextWriter.Null, TextWriter.Null));
            Assert.False(new LiteKill(root).On);
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.scope", "same-person"], root, TextWriter.Null, TextWriter.Null));
            Assert.False(new LiteKill(root).On);
            Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], root, TextWriter.Null, TextWriter.Null));
            Assert.True(new LiteKill(root).On);
        }
        finally { Directory.Delete(root, true); }
    }

    // "Never an agent" for the setting is the #67 refusal in Program.cs, which the ConfigCommands tests above do not reach (they call Run
    // directly). This runs the real host command line as a process that says it is inside an agent session (receipt: #4316 comment).
    [Theory]
    [InlineData("set", "agentMessaging.scope", "off")]
    [InlineData("set", "agentMessaging.remote", "own-account")]
    [InlineData("unset", "agentMessaging.remote", "")]
    public async Task Config_set_and_unset_from_an_agent_session_are_refused_and_change_nothing(string verb, string key, string value)
    {
        var local = Path.Combine(Path.GetTempPath(), "scope-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "agent-bridge-host.dll"));
            foreach (var arg in new[] { "config", verb, key, value }.Where(a => a.Length > 0)) { start.ArgumentList.Add(arg); }
            start.Environment["LOCALAPPDATA"] = local;
            start.Environment[AgentBridge.Local.AgentAncestry.SessionVariable] = "agent-session-1";
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("refused", error);
            Assert.Empty(Directory.GetFiles(local, "config.json", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(local, true); }
    }

    // A key written twice: the reader takes the last, as every other setting does, and a null scope is the same as an absent one.
    [Fact]
    public void A_key_written_twice_reads_as_its_last_value_and_a_null_scope_reads_as_absent()
    {
        Assert.False(Read("""{"scope":"same-person","scope":"off"}""").Settings.RemoteEnabled);
        Assert.True(Read("""{"scope":"off","scope":"same-person"}""").Settings.RemoteEnabled);
        Assert.Equal(Read("{}").Settings.Scope, Read("""{"scope":null}""").Settings.Scope);
        Assert.False(Read("""{"scope":null}""").Settings.RemoteSet);
    }

    [Fact]
    public void An_upgrade_default_is_not_an_explicit_on()
    {
        var (s, _) = Read("{}");
        Assert.False(s.RemoteSet);
        Assert.Equal(AgentMessagingSettings.SamePerson, s.Scope);
    }
}
