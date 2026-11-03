using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

public sealed class PublicationObservationTests : IDisposable
{
    const string Dashboard = "agent-decisions";
    readonly string folder = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "publication-" + Guid.NewGuid().ToString("N"));
    static readonly JsonElement Fixture = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication.json"))).RootElement.Clone();

    HaPublicationReceipts Receipts() => new(folder, new Uri("https://ha.test"), Dashboard);
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static HaPublicationPolicy Policy() => HaPublicationPolicy.Read(Fixture.GetProperty("policy"), Dashboard);
    public static IEnumerable<object[]> ObservationCases() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-publication-observation.json"))).RootElement.GetProperty("cases")
        .EnumerateArray().Select(c => new object[] { c.GetProperty("name").GetString()!, c.Clone() });
    static JsonElement Resources(HaPublicationPolicy policy) => JsonSerializer.SerializeToElement(new[]
    {
        new { id = "policy", type = "module", url = policy.ResourceUrl },
        new { id = "card", type = "module", url = "/local/agent-bridge-reply-card.js?v=1.0.0" },
    });

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(ObservationCases))]
    public async Task Actual_inventory_observation_agrees_with_the_recorded_legacy_boundary(string name, JsonElement example)
    {
        var receipts = Receipts();
        if (example.GetProperty("previous").ValueKind != JsonValueKind.Null)
        {
            receipts.Accept(HaPublicationPolicy.Read(example.GetProperty("previous"), Dashboard));
        }
        var ha = new FakeHomeAssistant
        {
            Resources = example.GetProperty("resources").Clone(),
            Dashboards = example.GetProperty("dashboards").Clone(),
            Dashboard = example.GetProperty("config").GetRawText(),
        };
        var errorCode = example.GetProperty("configError").GetString()!;
        if (errorCode == "config_not_found") { ha.Dashboard = null; }
        else if (errorCode.Length > 0) { ha.DashboardFails = new HaCommandException("lovelace/config", errorCode, "synthetic refusal"); }
        HaPublicationObservation? observation = null;
        var error = await Record.ExceptionAsync(async () => observation = await HaPublicationObservation.ReadAsync(ha, Dashboard, receipts));
        Assert.True(example.GetProperty("accepted").GetBoolean() == (error is null), $"{name}: {error}");
        if (name is "empty" or "legacy-dashboard" or "legacy-card" or "established" or "missing-config") { Assert.Null(error); }
        if (error is null)
        {
            Assert.Equal(example.GetProperty("kind").GetString(), observation!.Kind.ToString().ToLowerInvariant());
        }
        if (example.GetProperty("saved").GetBoolean()) { Assert.NotNull(receipts.Read()); }
        else if (example.GetProperty("previous").ValueKind == JsonValueKind.Null) { Assert.Null(receipts.Read()); }
    }

    [Fact]
    public async Task Actual_inventory_distinguishes_empty_legacy_and_established_without_writing_HA()
    {
        var ha = new FakeHomeAssistant();
        Assert.Equal(HaPublicationStateKind.Empty, (await HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts())).Kind);
        ha.Dashboards = Json("""[{"id":"old","url_path":"copilot-decisions"}]""");
        Assert.Equal(HaPublicationStateKind.Legacy, (await HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts())).Kind);
        ha.Resources = Resources(Policy());
        var observed = await HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts());
        Assert.Equal(HaPublicationStateKind.Established, observed.Kind);
        Assert.Equal(Policy().Hash, observed.Policy!.Hash);
        Assert.Equal(Policy().Hash, Receipts().Read()!.Hash);
        Assert.Empty(ha.Log);
    }

    [Fact]
    public async Task Restart_and_deleted_dashboard_do_not_forget_the_established_fence()
    {
        var ha = new FakeHomeAssistant { Resources = Resources(Policy()) };
        await HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts());
        ha.Resources = Json("[]");
        await Assert.ThrowsAsync<InvalidDataException>(() => HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts()));
        Assert.Equal(Policy().Hash, Receipts().Read()!.Hash);
    }

    [Fact]
    public async Task A_dashboard_receipt_prevents_bootstrap_even_without_a_local_receipt()
    {
        var ha = new FakeHomeAssistant
        {
            Dashboards = Json("""[{"id":"target","url_path":"agent-decisions"}]"""),
            Dashboard = """{"agent_bridge_publication":null}""",
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts()));
        Assert.Null(Receipts().Read());
    }

    [Theory]
    [InlineData("""[{"url":42}]""", "[]")]
    [InlineData("""[{"url":"/local/agent-bridge-reply-card.js","type":"module","id":""}]""", "[]")]
    [InlineData("""[{"url":"/local/agent-bridge-reply-card.js","type":"js","id":"card"}]""", "[]")]
    [InlineData("""[{"url":"/local/agent-bridge-reply-card.js","type":"module","id":"a"},{"url":"/other/agent-bridge-reply-card.js","type":"module","id":"b"}]""", "[]")]
    [InlineData("[]", """[{"url_path":"other"}]""")]
    [InlineData("[]", """[{"url_path":"agent-decisions","id":"a"},{"url_path":"agent-decisions","id":"b"}]""")]
    [InlineData("null", "[]")]
    public async Task Malformed_or_ambiguous_inventory_is_never_absence(string resources, string dashboards)
    {
        var ha = new FakeHomeAssistant { Resources = Json(resources), Dashboards = Json(dashboards) };
        await Assert.ThrowsAsync<InvalidDataException>(() => HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts()));
        Assert.Null(Receipts().Read());
    }

    [Fact]
    public async Task A_bad_dashboard_read_does_not_accept_an_otherwise_valid_new_policy()
    {
        var ha = new FakeHomeAssistant
        {
            Resources = Resources(Policy()),
            Dashboards = Json("""[{"url_path":"agent-decisions","id":"a"}]"""),
            DashboardFails = new HaCommandException("lovelace/config", "unauthorized", "denied"),
        };
        await Assert.ThrowsAsync<HaCommandException>(() => HaPublicationObservation.ReadAsync(ha, Dashboard, Receipts()));
        Assert.Null(Receipts().Read());
    }

    [Fact]
    public void Receipt_progression_is_rechecked_against_the_current_file_not_a_stale_read()
    {
        var original = Policy();
        var first = Receipts();
        first.Accept(original);
        var stale = Receipts().Read()!;
        var next = JsonNode.Parse(original.CanonicalJson)!.AsObject();
        next["generation"] = original.Generation + 1;
        next["writer"] = "replacement";
        var advanced = HaPublicationPolicy.Read(Json(next.ToJsonString()), Dashboard);
        Receipts().Accept(advanced);
        Assert.Throws<InvalidDataException>(() => first.Accept(stale));
        Assert.Equal(advanced.Hash, Receipts().Read()!.Hash);
        Assert.Empty(Directory.GetFiles(folder, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corrupt_receipts_are_not_missing_and_cancellation_does_not_replace_them()
    {
        Receipts().Accept(Policy());
        var path = Assert.Single(Directory.GetFiles(folder, "receipt.json", SearchOption.AllDirectories));
        File.WriteAllText(path, "{");
        Assert.Throws<InvalidDataException>(() => Receipts().Read());
        Assert.Throws<InvalidDataException>(() => Receipts().Accept(Policy()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Receipts().Accept(Policy(), cts.Token));
        Assert.Equal("{", File.ReadAllText(path));
    }
}
