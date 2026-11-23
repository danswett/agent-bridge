using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

// The client field on gate audit rows (S4 evidence, rows 4 and 5): two optional, closed-set fields the host writes from its verified
// roster for the principal device. Old rows must stay byte-identical, so the chain hash of an old-format row never changes.
public sealed class AuditClientFieldTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-client-" + Guid.NewGuid().ToString("N"));
    string Profile => Path.Combine(root, "default");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    static readonly Guid Op = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Incarnation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid Question = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // Lines 2 and 3 of an audit file written by the writer before this field existed (captured from main).
    const string OldIntent = "{\"at\":\"2026-11-20T12:00:00+00:00\",\"direction\":\"relay\",\"request\":\"11111111-1111-1111-1111-111111111111\",\"principal\":\"person:0123456789abcdef\",\"chain\":[],\"action\":\"session.answer\",\"target\":\"22222222-2222-2222-2222-222222222222\",\"outcome\":\"attempted\",\"reason\":\"none\",\"dataSha256\":null,\"textLength\":null,\"v\":1,\"previousSha256\":\"40137350a7b632ff10ee7c44b3afa90e15b606534628ea5bb3aee0d2ebff95ef\",\"gate\":{\"v\":1,\"operation\":\"11111111-1111-1111-1111-111111111111\",\"run\":\"00000000-0000-0000-0000-000000000000\",\"sequence\":1,\"phase\":\"intent\",\"path\":\"relay\",\"actorKind\":\"person-client\",\"incarnation\":\"22222222-2222-2222-2222-222222222222\",\"reasonCode\":\"none\",\"question\":\"33333333-3333-3333-3333-333333333333\",\"launchBinding\":null,\"grantBinding\":null,\"launchProcess\":null}}";
    const string OldTerminal = "{\"at\":\"2026-11-20T12:00:00+00:00\",\"direction\":\"relay\",\"request\":\"11111111-1111-1111-1111-111111111111\",\"principal\":\"person:0123456789abcdef\",\"chain\":[],\"action\":\"session.answer\",\"target\":\"22222222-2222-2222-2222-222222222222\",\"outcome\":\"delivered\",\"reason\":\"none\",\"dataSha256\":null,\"textLength\":null,\"v\":1,\"previousSha256\":\"172200cbec5fab4c9f6ca6e1226ea2a4ccd028b73ea591c0223877780eca8c56\",\"gate\":{\"v\":1,\"operation\":\"11111111-1111-1111-1111-111111111111\",\"run\":\"00000000-0000-0000-0000-000000000000\",\"sequence\":2,\"phase\":\"terminal\",\"path\":\"relay\",\"actorKind\":\"person-client\",\"incarnation\":\"22222222-2222-2222-2222-222222222222\",\"reasonCode\":\"none\",\"question\":\"33333333-3333-3333-3333-333333333333\",\"launchBinding\":null,\"grantBinding\":null,\"launchProcess\":null}}";

    static GateAuditMetadata Intent(string? kind = null, string? platform = null, string path = "relay", string actor = "person-client") =>
        new(1, Op, Guid.Empty, 1, "intent", path, actor, Incarnation, "none", Question, ClientKind: kind, ClientPlatform: platform);

    string[] Write(params GateAuditMetadata[] gates)
    {
        var log = new AgentAuditLog(Profile, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z")));
        foreach (var gate in gates)
        {
            var outcome = gate.Phase == "intent" ? "attempted" : "delivered";
            Assert.True(log.Append(new AgentAuditEntry("relay", Op.ToString(), "person:0123456789abcdef", [], "session.answer", Incarnation.ToString(), outcome, Gate: gate)));
        }
        return Directory.GetFiles(Path.Combine(Profile, AgentAuditLog.FolderName)).SelectMany(File.ReadAllLines).Where(l => l.Contains("\"session.answer\"")).ToArray();
    }

    [Fact]
    public void An_old_format_row_is_byte_identical_so_its_chain_hash_never_changes()
    {
        var lines = Write(Intent(), Intent() with { Phase = "terminal" });
        Assert.Equal([OldIntent, OldTerminal], lines);
        Assert.DoesNotContain("clientKind", string.Concat(lines));
        Assert.DoesNotContain("clientPlatform", string.Concat(lines));
        // The next row's previousSha256 is the hash of the raw line before it, so unchanged bytes mean an unchanged chain.
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OldIntent))).ToLowerInvariant(),
            JsonDocument.Parse(OldTerminal).RootElement.GetProperty("previousSha256").GetString());
        var (rows, complete) = GateAuditProjection.Load(Profile);
        Assert.True(complete);
        Assert.All(rows, r => { Assert.Null(r.Gate!.ClientKind); Assert.Null(r.Gate.ClientPlatform); });
    }

    [Fact]
    public void A_new_format_row_adds_only_the_two_fields_and_leaves_every_other_byte_alone()
    {
        var lines = Write(Intent("pwa", "ios"));
        var expected = OldIntent.Replace("\"launchProcess\":null}}", "\"launchProcess\":null,\"clientKind\":\"pwa\",\"clientPlatform\":\"ios\"}}");
        Assert.Equal(expected, Assert.Single(lines));
        var (rows, complete) = GateAuditProjection.Load(Profile);
        Assert.True(complete);
        Assert.Equal(("pwa", "ios"), (rows.Single().Gate!.ClientKind, rows.Single().Gate!.ClientPlatform));
    }

    [Fact]
    public void A_previous_version_reader_ignores_the_new_fields()
    {
        var line = OldIntent.Replace("\"launchProcess\":null}}", "\"launchProcess\":null,\"clientKind\":\"pwa\",\"clientPlatform\":\"ios\"}}");
        var old = JsonSerializer.Deserialize<PreviousRow>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("person:0123456789abcdef", old!.Principal);
        Assert.Equal("person-client", old.Gate!.ActorKind);
        Assert.Equal(Question, old.Gate.Question);
    }

    sealed record PreviousGate(int V, Guid Operation, Guid Run, long Sequence, string Phase, string Path, string ActorKind, Guid Incarnation, string ReasonCode, Guid? Question);
    sealed record PreviousRow(string Principal, string Action, string Outcome, PreviousGate? Gate);

    [Fact]
    public void The_fields_are_valid_only_together_from_the_closed_sets_on_a_relay_person_client_answer_or_stop()
    {
        var target = Incarnation.ToString();
        Assert.True(Intent().Valid("session.answer", target, "attempted"));
        Assert.True(Intent("pwa", "ios").Valid("session.answer", target, "attempted"));
        foreach (var kind in new[] { "app", "pwa", "phone" })
        {
            foreach (var platform in new[] { "windows", "macos", "linux", "ios", "ipados", "android" })
            { Assert.True(Intent(kind, platform).Valid("session.answer", target, "attempted"), $"{kind}/{platform}"); }
        }
        var stop = Intent("app", "windows") with { Question = null };
        Assert.True(stop.Valid("session.stop", target, "attempted"));
        Assert.False(Intent("pwa", null).Valid("session.answer", target, "attempted"));
        Assert.False(Intent(null, "ios").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("host", "windows").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("tablet", "ios").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("pwa", "symbian").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("PWA", "ios").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("pwa", "ios", path: "local").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("pwa", "ios", path: "legacy-ha", actor: "ha-component").Valid("session.answer", target, "attempted"));
        Assert.False(Intent("pwa", "ios", actor: "agent").Valid("session.answer", target, "attempted"));
        Assert.False((stop with { Question = null }).Valid("session.launch", target, "attempted"));
        Assert.False(Intent("pwa", "ios") with { Phase = "control" } is { } control && control.Valid("session.answer", "profile", "prepared"));
    }

    [Fact]
    public void A_writer_refuses_an_invalid_client_field_rather_than_writing_it()
    {
        var log = new AgentAuditLog(Profile, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z")));
        Assert.False(log.Append(new AgentAuditEntry("relay", Op.ToString(), "person:0123456789abcdef", [], "session.answer", Incarnation.ToString(), "attempted", Gate: Intent("tablet", "ios"))));
        Assert.False(log.Append(new AgentAuditEntry("local", Op.ToString(), "person:local", [], "session.answer", Incarnation.ToString(), "attempted", Gate: Intent("pwa", "ios", path: "local"))));
    }
}