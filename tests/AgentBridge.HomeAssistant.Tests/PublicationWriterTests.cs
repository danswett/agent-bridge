using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

public sealed class PublicationWriterTests : IDisposable
{
    const string Dashboard = "agent-decisions";
    readonly string folder = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "writer-" + Guid.NewGuid().ToString("N"));
    static readonly JsonElement Fixture = ReadFixture();
    static JsonElement ReadFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication.json")));
        return document.RootElement.Clone();
    }
    static HaPublicationPolicy Policy() => HaPublicationPolicy.Read(Fixture.GetProperty("policy"), Dashboard);
    static readonly HaPublicationIdentity Identity = new("test-authority", "test-writer", "test-writer", 3);
    static readonly string InputHash = new('a', 64);
    static JsonElement Json(JsonNode value) => JsonSerializer.SerializeToElement(value);
    static HaPublicationArtifact Artifact(string version, string hash) => HaPublicationArtifact.Read(Json(new JsonObject { ["version"] = version, ["hash"] = hash }));
    static string Card(string version) => "data:text/javascript;base64," +
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"const CARD_VERSION = '{version}';")) + $"#agent-bridge-reply-card.js?v={version}";
    HaPublicationWriter Writer(FakeHomeAssistant ha, HaPublicationIdentity? identity = null) =>
        new(ha, Dashboard, new(folder, new Uri("https://ha.test"), Dashboard), identity ?? Identity);
    static FakeHomeAssistant Established(HaPublicationPolicy? policy = null) => new()
    {
        Resources = Json(new JsonArray(new JsonObject { ["id"] = "policy", ["type"] = "module", ["url"] = (policy ?? Policy()).ResourceUrl })),
    };
    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Card_upgrade_advances_and_reads_back_the_fence_before_writing_the_artifact()
    {
        var ha = Established();
        var url = Card("2.0.0");
        await Writer(ha).PublishCardAsync(url);
        Assert.Equal(2, ha.PublicationWrites.Count);
        var first = ha.PublicationWrites[0];
        Assert.Equal("policy", first["resource_id"]!.GetValue<string>());
        var advanced = HaPublicationPolicy.ReadResource(first["url"]!.GetValue<string>(), Dashboard);
        Assert.Equal(HaPublicationArtifact.ReadInlineCard(url), advanced.HighCard);
        Assert.Equal(advanced.Card, advanced.HighCard);
        Assert.Equal(url, (await Writer(ha).ReadAsync()).CardUrl);
        ha.PublicationWrites.Clear();
        await Writer(ha).PublishCardAsync(url);
        Assert.Empty(ha.PublicationWrites);
    }

    [Fact]
    public async Task Unconfirmed_policy_or_artifact_writes_are_not_success()
    {
        var ha = Established();
        ha.PublicationWrite = _ => { };
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha).PublishCardAsync(Card("2.0.0")));
        Assert.Single(ha.PublicationWrites);
        Assert.Equal("policy", ha.PublicationWrites[0]["resource_id"]!.GetValue<string>());
        ha.PublicationWrites.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha).PublishCardAsync(Card("1.0.0")));
        Assert.Single(ha.PublicationWrites);
        Assert.Equal("lovelace/resources/create", ha.PublicationWrites[0]["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("observer", 3, "2.0.0", false)]
    [InlineData("test-writer", 2, "2.0.0", false)]
    [InlineData("test-writer", 3, "0.9.0", false)]
    [InlineData("test-writer", 3, "2.0.0", true)]
    public async Task Unauthorized_stale_or_pinned_upgrades_never_send_a_shared_write(string participant, int generation, string version, bool pin)
    {
        var policy = JsonNode.Parse(Policy().CanonicalJson)!.AsObject();
        if (pin) { policy["mode"] = "pin"; }
        var ha = Established(HaPublicationPolicy.Read(Json(policy), Dashboard));
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha, Identity with { Participant = participant, Generation = generation }).PublishCardAsync(Card(version)));
        Assert.Empty(ha.PublicationWrites);
    }

    [Fact]
    public async Task Automatic_publication_never_bootstraps_even_an_empty_instance()
    {
        var ha = new FakeHomeAssistant();
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha).PublishCardAsync(Card("1.0.0")));
        Assert.Empty(ha.PublicationWrites);
    }

    [Fact]
    public async Task Dashboard_publication_binds_actual_content_card_and_rendered_nodes_and_detects_later_overwrites()
    {
        var ha = Established();
        var writer = Writer(ha);
        await writer.PublishCardAsync(Card("1.0.0"));
        var config = new JsonObject { ["title"] = "Agent Sessions", ["views"] = new JsonArray(new JsonObject { ["cards"] = new JsonArray() }) };
        await writer.PublishDashboardAsync(config, InputHash, ["agent_bridge_one"], Policy().Render, Card("1.0.0"));
        Assert.Null(config["agent_bridge_publication"]);
        var state = await writer.ReadAsync();
        var proof = HaVerifiedPublication.Read(state);
        Assert.True(proof.Verified);
        Assert.Equal(InputHash, proof.InputHash);
        Assert.Contains("agent_bridge_one", proof.RenderedNodes);
        ha.Dashboard = ha.Dashboard!.Replace("Agent Sessions", "Changed", StringComparison.Ordinal);
        Assert.False(HaVerifiedPublication.Read(await writer.ReadAsync()).Verified);
        ha.Dashboard = state.Dashboard.Json;
        var resources = JsonNode.Parse(ha.Resources.GetRawText())!.AsArray();
        resources[1]!["url"] = Card("2.0.0");
        ha.Resources = Json(resources);
        Assert.False(HaVerifiedPublication.Read(await writer.ReadAsync()).Verified);
    }

    [Fact]
    public async Task Renderer_upgrade_fences_before_registration_and_saving_and_pins_independently_prove_the_actual_card()
    {
        var ha = Established();
        var renderer = Artifact("3.0.0", new string('c', 64));
        await Writer(ha).PublishDashboardAsync(new JsonObject { ["views"] = new JsonArray() }, InputHash, [], renderer, "");
        Assert.Equal(new[] { "lovelace/resources/update", "lovelace/dashboards/create", "lovelace/config/save" },
            ha.PublicationWrites.Select(c => c["type"]!.GetValue<string>()));
        var pinned = JsonNode.Parse(Policy().CanonicalJson)!.AsObject();
        pinned["mode"] = "pin";
        var other = Established(HaPublicationPolicy.Read(Json(pinned), Dashboard));
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(other).PublishDashboardAsync(new JsonObject(), InputHash, [], Policy().Render, ""));
        Assert.Empty(other.PublicationWrites);
    }

    [Fact]
    public async Task Config_write_readback_is_required_and_restart_keeps_the_accepted_renderer_high_water()
    {
        var ha = Established();
        var writer = Writer(ha);
        await writer.PublishDashboardAsync(new JsonObject { ["views"] = new JsonArray() }, InputHash, [], Artifact("3.0.0", new string('c', 64)), "");
        ha.PublicationWrites.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha).PublishDashboardAsync(new JsonObject(), InputHash, [], Policy().Render, ""));
        Assert.Empty(ha.PublicationWrites);
        ha.PublicationWrite = _ => { };
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha).PublishDashboardAsync(new JsonObject { ["title"] = "new" }, new string('d', 64), [], Artifact("3.0.0", new string('c', 64)), ""));
        Assert.Single(ha.PublicationWrites);
    }

    [Fact]
    public async Task Concurrent_writer_instances_preserve_both_fence_advances_through_artifact_readback()
    {
        var ha = Established();
        await Writer(ha).PublishCardAsync(Card("1.0.0"));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policyWrites = 0;
        ha.BeforePublicationWrite = async command =>
        {
            if (command["resource_id"]?.GetValue<string>() == "policy" && Interlocked.Increment(ref policyWrites) == 1)
            {
                paused.SetResult();
                await resume.Task;
            }
        };
        var renderer = Artifact("3.0.0", new string('c', 64));
        var rendering = Writer(ha).PublishDashboardAsync(new JsonObject { ["views"] = new JsonArray() }, InputHash, [], renderer, Card("1.0.0"));
        Task card = Task.CompletedTask;
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var otherProfile = new HaPublicationWriter(ha, Dashboard, new(Path.Combine(folder, "other-profile"), new Uri("https://ha.test"), Dashboard), Identity);
            card = otherProfile.PublishCardAsync(Card("2.0.0"));
            Assert.False(card.IsCompleted);
            using var cancel = new CancellationTokenSource();
            var canceled = otherProfile.PublishCardAsync(Card("3.0.0"), cancel.Token);
            Assert.False(canceled.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(rendering, card).WaitAsync(TimeSpan.FromSeconds(10));
        }
        var actual = (await Writer(ha).ReadAsync()).Policy!;
        Assert.Equal(renderer, actual.HighRender);
        Assert.Equal(HaPublicationArtifact.ReadInlineCard(Card("2.0.0")), actual.HighCard);
    }

    [Fact]
    public async Task A_card_changed_after_rendering_refuses_before_advancing_the_renderer_fence()
    {
        var ha = Established();
        await Writer(ha).PublishCardAsync(Card("1.0.0"));
        var rendered = HaPublicationRenderer.Render([], [new("alpha", "Alpha")], "", Card("1.0.0"));
        await Writer(ha).PublishCardAsync(Card("2.0.0"));
        ha.PublicationWrites.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(ha).PublishDashboardAsync(rendered, Artifact("3.0.0", new string('c', 64))));
        Assert.Empty(ha.PublicationWrites);
        Assert.Equal(Policy().Render, (await Writer(ha).ReadAsync()).Policy!.Render);
    }
}
