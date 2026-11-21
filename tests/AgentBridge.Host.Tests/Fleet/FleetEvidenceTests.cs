using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class FleetListTests
{
    [Theory]
    [InlineData("observedAt")]
    [InlineData("lastReplyAt")]
    public void Dangling_fraction_fails_the_evidence_read(string field) =>
        Assert.Throws<InvalidDataException>(() => SessionEvidence.Read(Body($$"""{"{{field}}":"2026-11-07T12:00:00.Z"}"""), futureAttention: true));

    // The relay checks with this same read, so each of these refuses the whole registry record.
    [Theory]
    [InlineData("""{"owner":{"state":"known"}}""")]
    [InlineData("""{"owner":{"state":"unknown","principal":"agent:x/y"}}""")]
    [InlineData("""{"owner":{"state":"known","principal":"bad\nline"}}""")]
    [InlineData("""{"owner":{"state":"none"}}""")]
    [InlineData("""{"pr":{"state":"known","number":0}}""")]
    [InlineData("""{"pr":{"state":"none","number":1}}""")]
    [InlineData("""{"pr":{"state":"maybe"}}""")]
    [InlineData("""{"finished":{"state":"known","value":"yes"}}""")]
    [InlineData("""{"finished":"unknown"}""")]
    public void Malformed_owner_pr_or_finished_fails_the_evidence_read(string evidence) =>
        Assert.Throws<InvalidDataException>(() => SessionEvidence.Read(Body(evidence)));

    async Task<JsonElement> EvidenceRow(string evidence)
    {
        var session = JsonNode.Parse("""{"node":"fixture","state":"idle","since":"2026-11-07T12:00:00Z"}""")!.AsObject();
        foreach (var p in JsonNode.Parse(evidence)!.AsObject()) { session[p.Key] = p.Value?.DeepClone(); }
        roster.Machines = RelayFleetRoster.Parse([JsonSerializer.SerializeToElement(new
        {
            machine = "fixture", online = true, record = new { sessions = new[] { session } },
        })]);
        return JsonSerializer.SerializeToElement(await Op().HandleAsync(Hello(AgentProtocol.ScopeRead), Body("""{"include":"content"}""")), Web)
            .GetProperty("sessions")[0];
    }

    [Fact]
    public async Task Fleet_list_includes_attention_kind_and_open_asks_when_present()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var row = await EvidenceRow("""{"attention":{"kind":"approval"},"openAsks":2,"stale":true}""");
        Assert.Equal("approval", row.GetProperty("attention").GetProperty("kind").GetString());
        Assert.Equal(2, row.GetProperty("openAsks").GetInt32());
        Assert.True(row.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Fleet_list_includes_composer_hash_length_and_provenance_never_text()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var hash = new string('a', 64);
        var row = await EvidenceRow($$"""{"unsentLength":7,"unsentHash":"{{hash}}","provenance":{"source":"host-observed","sendMarker":true,"unsentHash":"{{hash}}"},"senderVerified":true,"composerText":"private-composer","prompt":"private-question"}""");
        Assert.Equal(7, row.GetProperty("unsentLength").GetInt32());
        Assert.Equal(hash, row.GetProperty("unsentHash").GetString());
        Assert.Equal("host-observed", row.GetProperty("provenance").GetProperty("source").GetString());
        Assert.True(row.GetProperty("senderVerified").GetBoolean());
        Assert.DoesNotContain("private-", row.GetRawText());
    }

    [Fact]
    public async Task Fleet_list_omits_absent_fields_and_never_defaults_them()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var row = await EvidenceRow("{}");
        Assert.Equal(["machine", "machineOnline", "node", "secondsInState", "since", "state"],
            row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Fleet_list_includes_observedAt_per_session()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var row = await EvidenceRow("""{"observedAt":"2026-11-07T12:01:00Z","lastReplyAt":"2026-11-07T12:00:30Z"}""");
        Assert.Equal(Started.AddMinutes(1), row.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(Started.AddSeconds(30), row.GetProperty("lastReplyAt").GetDateTimeOffset());
        Assert.False(row.TryGetProperty("replyAt", out _));
    }

    [Fact]
    public async Task Future_attention_is_unknown_but_invalid_timestamps_fail_the_read()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Equal("unknown", (await EvidenceRow("""{"attention":{"kind":"future"}}"""))
            .GetProperty("attention").GetProperty("kind").GetString());
        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceRow("""{"observedAt":"not-a-time"}"""));
        await Assert.ThrowsAsync<InvalidDataException>(() => EvidenceRow("""{"lastReplyAt":"not-a-time"}"""));
    }
}
