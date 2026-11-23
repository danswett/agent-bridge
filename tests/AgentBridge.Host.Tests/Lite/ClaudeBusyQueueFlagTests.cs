using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

// agentMessaging.claudeBusyQueue (docs/message-delivery.md "Busy sends to Claude", #3828, Part of #2568): default off.
public sealed class ClaudeBusyQueueFlagTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "claude-busy-flag-" + Guid.NewGuid().ToString("N"));
    public ClaudeBusyQueueFlagTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    static bool Flag(string? json)
    {
        if (json is null) { return AgentMessagingSettings.Read(null).Settings.ClaudeBusyQueue; }
        using var doc = JsonDocument.Parse(json);
        return AgentMessagingSettings.Read(doc.RootElement).Settings.ClaudeBusyQueue;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"remote":"own-account"}""")]
    public void It_is_off_unless_the_key_is_set(string? json) => Assert.False(Flag(json));

    [Fact]
    public void It_is_on_only_with_an_explicit_own_account_remote_and_a_true_value()
    {
        Assert.True(Flag("""{"remote":"own-account","claudeBusyQueue":true}"""));
        Assert.False(Flag("""{"claudeBusyQueue":true}"""));
        Assert.False(Flag("""{"remote":"off","claudeBusyQueue":true}"""));
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void A_non_boolean_value_is_off_and_is_reported(string value)
    {
        using var doc = JsonDocument.Parse($$"""{"remote":"own-account","claudeBusyQueue":{{value}}}""");
        var (settings, problems) = AgentMessagingSettings.Read(doc.RootElement);
        Assert.False(settings.ClaudeBusyQueue);
        Assert.Contains(problems, p => p.Contains("claudeBusyQueue"));
    }

    [Fact]
    public void A_person_can_set_it_with_config_and_a_kill_or_unreadable_file_leaves_it_off()
    {
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], root, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.claudeBusyQueue", "true"], root, TextWriter.Null, TextWriter.Null));
        Assert.True(HostSettings.CurrentAgentMessaging(root).ClaudeBusyQueue);
        new LiteKill(root).Kill();
        Assert.False(HostSettings.CurrentAgentMessaging(root).ClaudeBusyQueue);
        File.WriteAllText(Path.Combine(root, HostSettings.FileName), "{ not json");
        Assert.False(HostSettings.CurrentAgentMessaging(root).ClaudeBusyQueue);
    }
}
