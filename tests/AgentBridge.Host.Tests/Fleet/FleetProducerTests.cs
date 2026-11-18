using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using Xunit;

public sealed class FleetProducerTests
{
    const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static async Task<JsonElement> Publish(ISessionSource source, TimeProvider time)
    {
        JsonElement record = default;
        await new FleetPublisher(source, "fixture", time: time).PublishAsync((r, _) => { record = r.Clone(); return Task.CompletedTask; });
        Assert.All(record.GetProperty("sessions").EnumerateArray(), s => Assert.True(s.TryGetProperty("observedAt", out _), "producer observation absent"));
        Assert.Null(RosterFields.Check(record, "fixture", out _, out _, out _));
        return Assert.Single(record.GetProperty("sessions").EnumerateArray());
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Transcript_facts_reach_publication_without_private_text_or_sender_inference(bool skim)
    {
        using var f = new Fixture(); f.Processes.Run(AgentKind.Copilot, 101);
        File.WriteAllLines(f.CopilotSession(Id, 101), [Copilot.User("private-question", 1), Copilot.Assistant("private-reply", 2),
            Copilot.Event("session.error", new { errorType = "session", message = "private-error" }, 3), Copilot.Assistant("\u26A0\uFE0F Turn error: private-error", 4), Copilot.TurnEnd(5)]);
        await using var source = f.Source(tailBytes: skim ? Encoding.UTF8.GetByteCount(Copilot.TurnEnd(5)) + Environment.NewLine.Length : 4 * 1024 * 1024); await source.RefreshAsync();
        var row = await Publish(source, f.Time);
        Assert.Equal(0, row.GetProperty("openAsks").GetInt32());
        Assert.Equal("unknown", row.GetProperty("attention").GetProperty("kind").GetString());
        Assert.Equal(Fixture.T0.AddSeconds(2), row.GetProperty("lastReplyAt").GetDateTimeOffset());
        Assert.Equal(f.Time.GetUtcNow(), row.GetProperty("observedAt").GetDateTimeOffset());
        Assert.False(row.GetProperty("stale").GetBoolean());
        Assert.DoesNotContain("private-", row.GetRawText());
        Assert.False(row.TryGetProperty("senderVerified", out _) || row.TryGetProperty("provenance", out _));
        Fixture.Append(source.Files(Id)!.Transcript!, Copilot.Assistant("", 6, reasoning: "private-reasoning"), Copilot.Assistant("private-delegate", 7, agentId: "delegate"));
        await source.RefreshAsync();
        Assert.Equal(Fixture.T0.AddSeconds(2), (await Publish(source, f.Time)).GetProperty("lastReplyAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Pending_permission_count_is_not_a_boolean_and_staleness_uses_host_observation()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 101);
        File.WriteAllLines(f.CopilotSession(Id, 101), [
            Copilot.Permission("first", "tool-1", Copilot.Prompts["commands"], 1),
            Copilot.Permission("second", "tool-2", Copilot.Prompts["commands"], 2)]);
        await using var source = f.Source();
        await source.RefreshAsync();
        var row = await Publish(source, f.Time);
        Assert.Equal(2, row.GetProperty("openAsks").GetInt32());
        Assert.Equal("permission", row.GetProperty("attention").GetProperty("kind").GetString());
        Assert.False(row.TryGetProperty("lastReplyAt", out _));
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await Publish(source, f.Time)).GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Composer_hash_is_host_keyed_stable_until_restart_and_only_evidence_reaches_the_roster()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 101);
        File.WriteAllText(f.CopilotSession(Id, 101), Copilot.User("private-question", 1));
        await using var source = f.Source();
        await source.RefreshAsync();
        var screens = new Screens { Rows = Box("private-composer") };
        using var capture = new FleetEvidenceCapture(screens, _ => true, f.Time);
        var snapshot = Assert.Single(await source.FleetSnapshotAsync());
        f.Time.Advance(TimeSpan.FromMinutes(10));
        var first = (await capture.ReadAsync(snapshot))!.Copy();
        Assert.Equal(snapshot.ObservedAt, first["observedAt"].GetDateTimeOffset());
        Assert.True(first["stale"].GetBoolean());
        var hash = first["unsentHash"].GetString();
        Assert.Matches("^[0-9a-f]{64}$", hash!);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("private-composer"))), hash);
        Assert.Equal(hash, (await capture.ReadAsync(snapshot))!.Copy()["unsentHash"].GetString());
        using var restarted = new FleetEvidenceCapture(screens, _ => true, f.Time);
        Assert.NotEqual(hash, (await restarted.ReadAsync(snapshot))!.Copy()["unsentHash"].GetString());
        JsonElement record = default;
        await new FleetPublisher(source, "fixture", time: f.Time, observe: capture.ReadAsync)
            .PublishAsync((r, _) => { record = r.Clone(); return Task.CompletedTask; });
        Assert.Null(RosterFields.Check(record, "fixture", out _, out _, out _));
        var row = record.GetProperty("sessions")[0];
        Assert.Equal("private-composer".Length, row.GetProperty("unsentLength").GetInt32());
        Assert.Equal(hash, row.GetProperty("provenance").GetProperty("unsentHash").GetString());
        Assert.False(row.GetProperty("provenance").TryGetProperty("sendMarker", out _));
        Assert.False(row.TryGetProperty("senderVerified", out _));
        Assert.DoesNotContain("private-", record.GetRawText());
        screens.Rows = Box("changed");
        Assert.NotEqual(hash, (await capture.ReadAsync(snapshot))!.Copy()["unsentHash"].GetString());
        capture.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.ReadAsync(snapshot));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("shell")]
    [InlineData("recycled")]
    [InlineData("narrow")]
    public async Task Unobservable_or_unbound_composer_never_gets_a_hash_or_provenance(string mode)
    {
        var screens = new Screens
        {
            Rows = mode == "missing" ? null : mode == "partial"
            ? ["\u2503 private-partial", "\u2579\u2580\u2580", "Interactive"] : mode == "narrow"
            ? [.. Box("private-composer")[..^1], " Working esc interrupt"] : Box(mode == "shell" ? "Execute shell command" : "private-composer")
        };
        var calls = 0;
        using var capture = new FleetEvidenceCapture(screens, _ => mode != "recycled" || ++calls == 1);
        var snapshot = new FleetSessionSnapshot(FleetPublisherTests.NewSession(1), new(101, Fixture.T0));
        var fields = (await capture.ReadAsync(snapshot))?.Copy() ?? [];
        Assert.DoesNotContain("unsentLength", fields.Keys);
        Assert.DoesNotContain("unsentHash", fields.Keys);
        Assert.DoesNotContain("provenance", fields.Keys);
    }

    [Fact]
    public async Task An_observed_empty_composer_is_zero_not_unknown_and_unrecognized_screens_stay_unknown()
    {
        var screens = new Screens { Rows = Box("") };
        using var capture = new FleetEvidenceCapture(screens, _ => true);
        var snapshot = new FleetSessionSnapshot(FleetPublisherTests.NewSession(1), new(101, Fixture.T0));
        var fields = (await capture.ReadAsync(snapshot))!.Copy();
        Assert.False(fields.ContainsKey("observedAt"));
        Assert.False(fields.ContainsKey("stale"));
        Assert.Equal(0, fields["unsentLength"].GetInt32());
        Assert.False(fields.ContainsKey("unsentHash"));
        screens.Rows = ["unrecognized screen"];
        Assert.Equal("unknown", (await capture.ReadAsync(snapshot))!.Copy()["attention"].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_changed_session_binding_or_cancelled_read_never_publishes_composer_evidence()
    {
        var source = new FleetPublisherTests.Source();
        SessionProcess binding = new(101, Fixture.T0);
        source.Binding = _ => binding;
        var sent = false;
        var publisher = new FleetPublisher(source, "fixture", observe: (snapshot, ct) =>
        {
            binding = new(202, Fixture.T0);
            return Task.FromResult<SessionEvidence?>(null);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync((_, _) => { sent = true; return Task.CompletedTask; }));
        Assert.False(sent);
        using var stop = new CancellationTokenSource();
        var screens = new Screens { OnRead = () => { stop.Cancel(); return new ConsoleScreen(Box("private-composer"), null); } };
        using var capture = new FleetEvidenceCapture(screens, _ => true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            capture.ReadAsync(new(source.Sessions[0], binding), stop.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_locked_transcripts_do_not_renew_source_freshness(bool locked)
    {
        using var f = new Fixture(); f.Processes.Run(AgentKind.Copilot, 101);
        var path = f.CopilotSession(Id, 101);
        File.WriteAllText(path, Copilot.Permission("first", "tool-1", Copilot.Prompts["commands"], 1));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        await using var source = f.Source(); await source.RefreshAsync();
        var before = Assert.Single(await source.FleetSnapshotAsync());
        using var held = locked ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (!locked) { File.Delete(path); }
        f.Time.Advance(TimeSpan.FromMinutes(10));
        await source.RefreshAsync();
        var after = Assert.Single(await source.FleetSnapshotAsync());
        Assert.Equal(before.ObservedAt, after.ObservedAt);
        Assert.True((bool)FleetEvidenceCapture.Facts(after, f.Time.GetUtcNow())["stale"]);
    }

    static string[] Box(string content) => ["\u257B\u2584\u2584\u2584", "\u2503 " + content, "\u2579\u2580\u2580\u2580", " Interactive \u00B7 / commands \u00B7 @ files"];
    sealed class Screens : IConsoleInjector
    {
        public IReadOnlyList<string>? Rows;
        public Func<ConsoleScreen>? OnRead;
        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default) => Task.FromResult(OnRead?.Invoke() ?? new ConsoleScreen(Rows, null));
        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default) => throw new InvalidOperationException("must never type");
    }
}
