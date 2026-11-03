using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class PublicationRuntimeTests : IDisposable
{
    const string Dashboard = "agent-decisions";
    const string A = "publication-local-session";
    static readonly Uri Instance = new("https://ha.test");
    readonly string folder = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "native-runtime-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> messages = [];
    static readonly HaPublicationSettings Writer = new() { Enabled = true, Authority = "test-authority", Participant = "test-writer", Writer = "test-writer", Generation = 3 };

    public PublicationRuntimeTests()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication.json")));
        var initial = JsonNode.Parse(fixture.RootElement.GetProperty("policy").GetRawText())!;
        initial["render"]!["version"] = "1.13.0";
        initial["highRender"]!["version"] = "1.13.0";
        var policy = HaPublicationPolicy.Read(JsonSerializer.SerializeToElement(initial), Dashboard);
        ha.Resources = JsonSerializer.SerializeToElement(new[] { new { id = "policy", type = "module", url = policy.ResourceUrl } });
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    HaConnector Connector(HaPublicationSettings? publication = null, Func<string?>? legacy = null) => new(new()
    {
        MachineName = "Alpha", Version = "0.1.0", ProfileFolder = folder, BaseUrl = Instance,
        Publication = publication ?? Writer, StateFile = Path.Combine(folder, "ha-connector.json"),
        Reconcile = TimeSpan.Zero, PinDelay = TimeSpan.Zero, RetireGrace = TimeSpan.FromSeconds(15),
    }, ha, sessions, legacy ?? (() => null), log: messages.Add, time: time);

    async Task<HaVerifiedPublication> Proof()
        => HaVerifiedPublication.Read(await HaPublicationObservation.ReadAsync(ha, Dashboard, new(folder, Instance, Dashboard)));

    void Peer(string slug, string name, bool online, params HaFleetSession[] descriptors)
    {
        var attrs = new
        {
            machine = name, machine_slug = slug,
            capabilities = new { profile = true, resume = false, agent = true, tuning = true, permissions = true, detailed = true, dev = false },
            sessions = descriptors.Select(s => new { node = s.Node, name = s.Name, machine = s.Machine, kind = s.Kind }).ToArray(),
        };
        var sensor = $"sensor.agent_bridge_{slug}_sessions";
        ha.States[sensor] = new(sensor, descriptors.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.SerializeToElement(attrs));
        var liveness = $"binary_sensor.agent_bridge_{slug}_online";
        ha.States[liveness] = new(liveness, online ? "on" : "off", JsonSerializer.SerializeToElement(new { }));
    }

    [Fact]
    public async Task Opted_in_writer_reconciles_the_whole_mixed_fleet_and_repairs_actual_overwrites_after_restart()
    {
        sessions.Set(FakeSessions.Copilot(A));
        Peer("beta", "Beta", true, new("agent_bridge_remote", "Remote", "Beta", "claude"), new("agent_bridge_mcp", "MCP", "Shared", "mcp"));
        Peer("gamma", "Gamma", true, new HaFleetSession("agent_bridge_mcp", "MCP", "Shared", "mcp"));
        Peer("offline", "Offline", false, new HaFleetSession("agent_bridge_old", "Old", "Offline", "copilot"));
        await using (var connector = Connector())
        {
            await connector.TickAsync();
            var proof = await Proof();
            Assert.True(proof.Verified, string.Join("\n", messages));
            Assert.True(proof.RenderedNodes.SetEquals([HaContract.NodeId(A), "agent_bridge_remote", "agent_bridge_mcp"]));
            Assert.Contains("Offline", ha.Dashboard!, StringComparison.Ordinal);
            Assert.Single(ha.MachineSelectors.EnumerateArray());
            Assert.Equal("Alpha", ha.States["input_select.agent_bridge_target_machine"].State);
            ha.PublicationWrites.Clear();
            await connector.TickAsync();
            Assert.Empty(ha.PublicationWrites);
        }
        ha.Dashboard = JsonNode.Parse(ha.Dashboard!)!.AsObject().ToJsonString().Replace("Agent Sessions", "Overwritten", StringComparison.Ordinal);
        var card = JsonNode.Parse(ha.Resources.GetRawText())!.AsArray().OfType<JsonObject>().Single(r => r["id"]!.GetValue<string>() != "policy");
        card["url"] = "/local/agent-bridge-reply-card.js?v=1.42.0";
        var resources = JsonNode.Parse(ha.Resources.GetRawText())!.AsArray();
        resources[1] = card.DeepClone();
        ha.Resources = JsonSerializer.SerializeToElement(resources);
        await using (var restarted = Connector())
        {
            await restarted.TickAsync();
            Assert.True((await Proof()).Verified, string.Join("\n", messages));
            Assert.DoesNotContain("Overwritten", ha.Dashboard!, StringComparison.Ordinal);
            Assert.Contains(ha.PublicationWrites, w => w["type"]!.GetValue<string>() == "lovelace/resources/update");
            Peer("beta", "Beta", false);
            Peer("gamma", "Gamma", false);
            await restarted.TickAsync();
            Assert.Empty(ha.MachineSelectors.EnumerateArray());
            Assert.True((await Proof()).RenderedNodes.SetEquals([HaContract.NodeId(A)]));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Off_nonwriter_and_missing_policy_cannot_bootstrap_or_publish_shared_artifacts(bool enabled, bool missingPolicy)
    {
        if (missingPolicy) { ha.Resources = JsonSerializer.SerializeToElement(Array.Empty<object>()); }
        await using var connector = Connector(Writer with { Enabled = enabled, Participant = "observer" });
        await connector.TickAsync();
        Assert.Empty(ha.PublicationWrites);
        Assert.Null(ha.Dashboard);
    }

    [Fact]
    public async Task Malformed_peer_inventory_and_a_mid_pass_legacy_start_both_stop_shared_writes()
    {
        Peer("beta", "Wrong identity", true);
        await using (var malformed = Connector())
        {
            await malformed.TickAsync();
            Assert.Empty(ha.PublicationWrites);
            Assert.Contains(messages, m => m.Contains("identity", StringComparison.Ordinal));
        }
        ha.States.Remove("sensor.agent_bridge_beta_sessions");
        await using var fenced = Connector(legacy: () => ha.PublicationWrites.Count > 0 ? "legacy daemon running" : null);
        await fenced.TickAsync();
        var only = Assert.Single(ha.PublicationWrites);
        Assert.Equal("policy", only["resource_id"]!.GetValue<string>());
        Assert.Null(ha.Dashboard);
    }

    [Theory]
    [InlineData("{\"profile\":\"true\"}")]
    [InlineData("{\"profile\":true,\"profile\":false}")]
    [InlineData("[]")]
    public async Task Malformed_peer_capabilities_refuse_the_whole_fleet_before_any_shared_write(string capabilities)
    {
        Peer("beta", "Beta", true);
        const string entity = "sensor.agent_bridge_beta_sessions";
        var attributes = ha.States[entity].Attributes.GetRawText();
        using var parsed = JsonDocument.Parse(attributes);
        var original = parsed.RootElement.GetProperty("capabilities").GetRawText();
        ha.States[entity] = ha.States[entity] with { Attributes = JsonDocument.Parse(attributes.Replace(original, capabilities, StringComparison.Ordinal)).RootElement.Clone() };
        await using var connector = Connector();
        await connector.TickAsync();
        Assert.Empty(ha.PublicationWrites);
        Assert.Null(ha.Dashboard);
    }

    [Fact]
    public async Task Own_stale_retained_sensor_is_ignored_but_conflicting_peer_session_nodes_are_not()
    {
        sessions.Set(FakeSessions.Copilot(A));
        ha.States["sensor.agent_bridge_alpha_sessions"] = new("sensor.agent_bridge_alpha_sessions", "unknown", JsonSerializer.SerializeToElement(new { }));
        await using var connector = Connector();
        await connector.TickAsync();
        Assert.True((await Proof()).Verified, string.Join("\n", messages));
        ha.PublicationWrites.Clear();
        Peer("beta", "Beta", true, new HaFleetSession(HaContract.NodeId(A), "Conflicting", "Beta", "copilot"));
        await connector.TickAsync();
        Assert.Empty(ha.PublicationWrites);
        Assert.Contains(messages, m => m.Contains("Conflicting peer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selector_creation_cleans_only_its_returned_duplicate_and_never_renders_a_wrong_control()
    {
        Peer("beta", "Beta", true);
        ha.MachineSelectors = JsonSerializer.SerializeToElement(new[] { new { id = "unrelated", name = "Unrelated", options = new[] { "Keep" } } });
        ha.SelectorCreatedId = id => id + "_2";
        await using var connector = Connector();
        await connector.TickAsync();
        Assert.Null(ha.Dashboard);
        Assert.Equal("unrelated", Assert.Single(ha.MachineSelectors.EnumerateArray()).GetProperty("id").GetString());
        var deletion = Assert.Single(ha.PublicationWrites, w => w["type"]!.GetValue<string>() == "input_select/delete");
        Assert.Equal("agent_bridge_target_machine_2", deletion["input_select_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("[{\"id\":\"other\",\"name\":\"Agent bridge target machine\",\"options\":[\"Keep\"]}]")]
    [InlineData("[{\"id\":\"other\",\"id\":\"agent_bridge_target_machine\",\"options\":[\"Keep\"]}]")]
    [InlineData("[{\"id\":\"agent_bridge_target_machine\",\"options\":null}]")]
    [InlineData("{}")]
    public async Task Occupied_or_malformed_selector_inventory_never_creates_partial_controls(string inventory)
    {
        Peer("beta", "Beta", true);
        ha.MachineSelectors = JsonDocument.Parse(inventory).RootElement.Clone();
        await using var connector = Connector();
        await connector.TickAsync();
        Assert.DoesNotContain(ha.PublicationWrites, w => w["type"]!.GetValue<string>().StartsWith("input_select/", StringComparison.Ordinal));
        Assert.Null(ha.Dashboard);
    }

    [Fact]
    public async Task Selector_selection_is_repaired_again_after_an_asynchronous_options_reset()
    {
        Peer("beta", "Beta", true);
        await using var connector = Connector();
        await connector.TickAsync();
        const string entity = "input_select.agent_bridge_target_machine";
        ha.States[entity] = ha.States[entity] with { State = "unavailable" };
        ha.PublicationWrites.Clear();
        await connector.TickAsync();
        Assert.Equal("Alpha", ha.States[entity].State);
        Assert.Empty(ha.PublicationWrites);
        Assert.True((await Proof()).Verified);
    }

    [Fact]
    public async Task Selector_and_card_transactions_share_the_endpoint_gate_through_actual_control_readback()
    {
        var identity = new HaPublicationIdentity(Writer.Authority, Writer.Participant, Writer.Writer, Writer.Generation);
        var writer = new HaPublicationWriter(ha, Dashboard, new(folder, Instance, Dashboard), identity);
        await writer.PublishCardAsync(HaPublicationRenderer.CardUrl);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ha.BeforePublicationWrite = async command =>
        {
            if (command["type"]!.GetValue<string>() == "input_select/create")
            {
                paused.SetResult();
                await resume.Task;
            }
        };
        var selector = writer.ReconcileSelectorAsync([new("alpha", "Alpha"), new("beta", "Beta")], HaPublicationRenderer.Artifact);
        Task card = Task.CompletedTask;
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = new HaPublicationWriter(ha, Dashboard, new(Path.Combine(folder, "other-profile"), Instance, Dashboard), identity);
            var url = "data:text/javascript;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("const CARD_VERSION = '1.43.0';")) +
                "#agent-bridge-reply-card.js?v=1.43.0";
            card = other.PublishCardAsync(url);
            Assert.False(card.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(selector, card).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal("input_select.agent_bridge_target_machine", await selector);
        var actual = (await writer.ReadAsync()).Policy!;
        Assert.Equal(HaPublicationRenderer.Artifact, actual.HighRender);
        Assert.Equal("1.43.0", actual.HighCard.VersionText);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"broken\"")]
    public async Task Authorized_writer_repairs_malformed_dashboard_receipts_without_weakening_policy_observation(string receipt)
    {
        await using var connector = Connector();
        await connector.TickAsync();
        var tampered = JsonNode.Parse(ha.Dashboard!)!.AsObject();
        tampered["agent_bridge_publication"] = JsonNode.Parse(receipt);
        ha.Dashboard = tampered.ToJsonString();
        ha.PublicationWrites.Clear();
        await connector.TickAsync();
        Assert.True((await Proof()).Verified, string.Join("\n", messages));
        Assert.Contains(ha.PublicationWrites, w => w["type"]!.GetValue<string>() == "lovelace/config/save");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_and_authentication_held_intervals_never_count_towards_retirement_grace(bool authentication)
    {
        var paused = false;
        sessions.Set(FakeSessions.Copilot(A));
        await using var connector = Connector(legacy: () => paused && !authentication ? "legacy daemon running" : null);
        await connector.TickAsync();
        sessions.End(A);
        await connector.TickAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        paused = true;
        if (authentication) { ha.Refused = "credential held"; }
        await connector.TickAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        paused = false;
        ha.Refused = null;
        await connector.TickAsync();
        Assert.Contains(HaContract.NodeId(A), connector.PendingRetirement);
        time.Advance(TimeSpan.FromSeconds(14));
        await connector.TickAsync();
        Assert.Contains(HaContract.NodeId(A), connector.PendingRetirement);
        time.Advance(TimeSpan.FromSeconds(2));
        await connector.TickAsync();
        Assert.Empty(connector.PendingRetirement);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Even_nonwriters_require_actual_publication_proof_and_a_fresh_grace_period_before_retirement(bool enabled, bool lostPolicy)
    {
        sessions.Set(FakeSessions.Copilot(A));
        await using (var writer = Connector())
        {
            await writer.TickAsync();
            sessions.End(A);
            await writer.TickAsync();
            Assert.Contains(HaContract.NodeId(A), writer.PendingRetirement);
        }
        var saved = ha.Dashboard;
        var resources = ha.Resources;
        await using var observer = Connector(Writer with { Enabled = enabled, Participant = "observer" });
        ha.PublicationWrites.Clear();
        await observer.TickAsync();
        if (lostPolicy)
        {
            ha.Resources = JsonSerializer.SerializeToElement(resources.EnumerateArray().Where(e => e.GetProperty("id").GetString() != "policy").ToArray());
        }
        else { ha.Dashboard = JsonNode.Parse(saved!)!.AsObject().ToJsonString().Replace("Agent Sessions", "Tampered", StringComparison.Ordinal); }
        time.Advance(TimeSpan.FromSeconds(30));
        await observer.TickAsync();
        Assert.Contains(HaContract.NodeId(A), observer.PendingRetirement);
        if (lostPolicy) { Assert.Contains(messages, m => m.Contains("policy was lost", StringComparison.Ordinal)); }
        ha.Dashboard = saved;
        ha.Resources = resources;
        await observer.TickAsync();
        time.Advance(TimeSpan.FromSeconds(14));
        await observer.TickAsync();
        Assert.Contains(HaContract.NodeId(A), observer.PendingRetirement);
        time.Advance(TimeSpan.FromSeconds(2));
        await observer.TickAsync();
        Assert.Empty(observer.PendingRetirement);
        Assert.Empty(ha.PublicationWrites);
    }
}
