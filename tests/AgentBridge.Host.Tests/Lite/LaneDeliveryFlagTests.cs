using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class LaneDeliveryFlagTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "lane-flag-" + Guid.NewGuid().ToString("N"));
    public LaneDeliveryFlagTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    static bool Lane(string? json)
    {
        if (json is null) { return AgentMessagingSettings.Read(null).Settings.LaneDelivery; }
        using var doc = JsonDocument.Parse(json);
        return AgentMessagingSettings.Read(doc.RootElement).Settings.LaneDelivery;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"remote":"own-account"}""")]
    public void Lane_delivery_is_off_unless_the_key_is_set(string? json) => Assert.False(Lane(json));

    [Fact]
    public void Lane_delivery_is_on_only_with_an_explicit_own_account_remote_and_a_true_value()
    {
        Assert.True(Lane("""{"remote":"own-account","laneDelivery":true}"""));
        Assert.False(Lane("""{"laneDelivery":true}"""));
        Assert.False(Lane("""{"remote":"off","laneDelivery":true}"""));
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void A_non_boolean_value_is_off_and_is_reported(string value)
    {
        using var doc = JsonDocument.Parse($$"""{"remote":"own-account","laneDelivery":{{value}}}""");
        var (settings, problems) = AgentMessagingSettings.Read(doc.RootElement);
        Assert.False(settings.LaneDelivery);
        Assert.Contains(problems, p => p.Contains("laneDelivery"));
    }

    [Fact]
    public void A_person_can_set_the_flag_with_config_and_a_kill_or_unreadable_file_leaves_it_off()
    {
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], root, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.laneDelivery", "true"], root, TextWriter.Null, TextWriter.Null));
        Assert.True(HostSettings.CurrentAgentMessaging(root).LaneDelivery);
        new LiteKill(root).Kill();
        Assert.False(HostSettings.CurrentAgentMessaging(root).LaneDelivery);
        File.WriteAllText(Path.Combine(root, HostSettings.FileName), "{ not json");
        Assert.False(HostSettings.CurrentAgentMessaging(root).LaneDelivery);
    }
}
