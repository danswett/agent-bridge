using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

public sealed class RosterEvidenceTests
{
    static string? Check(string evidence)
    {
        var record = JsonNode.Parse("""{"v":1,"machine":"fixture","epoch":"aaaaaaaaaaaaaaaa","seq":1,"at":"2026-11-07T12:00:00Z","truncated":false,"sessions":[]}""")!.AsObject();
        var session = JsonNode.Parse("""{"state":"idle","since":"2026-11-07T12:00:00Z"}""")!.AsObject();
        session["id"] = new string('a', 16); session["node"] = "agent_bridge_" + new string('a', 16);
        foreach (var p in JsonNode.Parse(evidence)!.AsObject()) { session[p.Key] = p.Value?.DeepClone(); }
        record["sessions"]!.AsArray().Add(session);
        return RosterFields.Check(JsonSerializer.SerializeToElement(record), "fixture", out _, out _, out _);
    }

    [Theory]
    [InlineData("""{"attention":{"kind":"approval"},"openAsks":2,"stale":false}""")]
    [InlineData("""{"attention":{"kind":"none"}}""")]
    [InlineData("""{"attention":{"kind":"question"}}""")]
    [InlineData("""{"attention":{"kind":"trust"}}""")]
    [InlineData("""{"attention":{"kind":"permission"}}""")]
    [InlineData("""{"attention":{"kind":"unknown"}}""")]
    [InlineData("""{"observedAt":"2026-11-07T12:00:00Z","lastReplyAt":"2026-11-07T11:59:00Z","senderVerified":"unknown"}""")]
    [InlineData("""{"unsentLength":0,"senderVerified":false}""")]
    public void Valid_optional_evidence_is_accepted(string evidence) => Assert.Null(Check(evidence));

    [Fact]
    public void Bound_composer_provenance_is_accepted_without_any_composer_text()
    {
        var h = new string('a', 64);
        Assert.Null(Check($$"""{"unsentLength":1,"unsentHash":"{{h}}","provenance":{"source":"relay-supplied","sendMarker":true,"unsentHash":"{{h}}"},"senderVerified":true}"""));
    }

    [Theory]
    [InlineData("""{"attention":{"kind":"future"}}""")]
    [InlineData("""{"attention":{"kind":"none","message":"private"}}""")]
    [InlineData("""{"openAsks":true}""")]
    [InlineData("""{"openAsks":-1}""")]
    [InlineData("""{"openAsks":2147483648}""")]
    [InlineData("""{"unsentLength":1.5}""")]
    [InlineData("""{"unsentHash":"short"}""")]
    [InlineData("""{"observedAt":null}""")]
    [InlineData("""{"observedAt":"2026-11-07T12:00:00+01:00"}""")]
    [InlineData("""{"lastReplyAt":"bad"}""")]
    [InlineData("""{"senderVerified":"true"}""")]
    [InlineData("""{"provenance":{"source":"owner","sendMarker":true,"unsentHash":"private"}}""")]
    [InlineData("""{"provenance":{"source":"host-observed","sendMarker":"true"}}""")]
    [InlineData("""{"provenance":{"source":"host-observed","prompt":"private"}}""")]
    [InlineData("""{"provenance":{}}""")]
    [InlineData("""{"attention":{"kind":"a-future-kind-that-is-more-than-thirty-two-characters"}}""")]
    [InlineData("""{"composerText":"private"}""")]
    public void Malformed_unknown_or_private_evidence_is_a_bad_record(string evidence) =>
        Assert.Equal("bad-record", Check(evidence));
}
