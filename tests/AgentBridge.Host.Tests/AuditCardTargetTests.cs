using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

// The client-reported card target (owner decision on #4044): evidence only, written as received once its shape passes, never read for a decision.
public sealed class AuditCardTargetTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-card-" + Guid.NewGuid().ToString("N"));
    string Profile => Path.Combine(root, "default");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    static readonly Guid Op = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Incarnation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid Question = Guid.Parse("33333333-3333-3333-3333-333333333333");
    const string Card = "44444444-4444-4444-4444-444444444444";

    static GateAuditMetadata Gate(string? card, string phase = "intent", string path = "relay", string actor = "person-client") =>
        new(1, Op, Guid.Empty, 1, phase, path, actor, Incarnation, "none", Question, ClientReportedCardTarget: card);

    string[] Write(string? card)
    {
        var log = new AgentAuditLog(Profile, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z")));
        Assert.True(log.Append(new AgentAuditEntry("relay", Op.ToString(), "person:0123456789abcdef", [], "session.answer", Incarnation.ToString(), "attempted", Gate: Gate(card))));
        return Directory.GetFiles(Path.Combine(Profile, AgentAuditLog.FolderName)).SelectMany(File.ReadAllLines).Where(l => l.Contains("\"session.answer\"")).ToArray();
    }

    [Fact]
    public void A_card_target_adds_only_its_own_field_and_a_row_without_one_keeps_its_bytes()
    {
        var plain = Assert.Single(Write(null));
        Assert.DoesNotContain("clientReportedCardTarget", plain);
        Directory.Delete(root, true);
        var with = Assert.Single(Write(Card));
        Assert.Equal(plain.Replace("\"launchProcess\":null}}", $"\"launchProcess\":null,\"clientReportedCardTarget\":\"{Card}\"}}}}"), with);
    }

    [Fact]
    public void The_field_is_valid_only_on_a_person_client_answer_row_and_only_in_a_plain_shape()
    {
        var target = Incarnation.ToString();
        Assert.True(Gate(Card).Valid("session.answer", target, "attempted"));
        Assert.True(Gate(Card, path: "local").Valid("session.answer", target, "attempted"));
        Assert.True(Gate("other-session_1.x").Valid("session.answer", target, "attempted"));
        Assert.True(Gate(new string('a', 64)).Valid("session.answer", target, "attempted"));
        foreach (var bad in new[] { "", new string('a', 65), "has space", "line\nbreak", "caf\u00e9", "semi;colon", "a/b" })
        { Assert.False(Gate(bad).Valid("session.answer", target, "attempted"), $"[{bad}]"); }
        Assert.False((Gate(Card) with { Question = null }).Valid("session.stop", target, "attempted"));
        Assert.False(Gate(Card, actor: "agent").Valid("session.answer", target, "attempted"));
        Assert.False(Gate(Card, path: "legacy-ha", actor: "ha-component").Valid("session.answer", target, "attempted"));
        Assert.False((Gate(Card) with { Phase = "control" }).Valid("session.answer", "profile", "prepared"));
    }

    [Fact]
    public void A_writer_refuses_an_invalid_card_target_rather_than_writing_it()
    {
        var log = new AgentAuditLog(Profile, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z")));
        Assert.False(log.Append(new AgentAuditEntry("relay", Op.ToString(), "person:0123456789abcdef", [], "session.answer", Incarnation.ToString(), "attempted", Gate: Gate("bad value"))));
    }

    // Decision independence: the evidence readers give the same answer for the same rows with and without the field, and for any value in it.
    [Theory]
    [InlineData(Card)]
    [InlineData("a-different-session")]
    public void The_gate_projection_and_the_evidence_readers_ignore_the_field(string card)
    {
        var log = new AgentAuditLog(Profile, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z")));
        foreach (var phase in new[] { "intent", "terminal" })
        {
            Assert.True(log.Append(new AgentAuditEntry("relay", Op.ToString(), "person:0123456789abcdef", [], "session.answer", Incarnation.ToString(),
                phase == "intent" ? "attempted" : "delivered", Gate: Gate(card, phase))));
        }
        var (rows, complete) = GateAuditProjection.Load(Profile);
        Assert.All(rows, r => Assert.Equal(card, r.Gate!.ClientReportedCardTarget));
        var stripped = rows.Select(r => r with { Gate = r.Gate! with { ClientReportedCardTarget = null } }).ToArray();
        var run = Guid.NewGuid();
        var (a, b) = (GateAuditProjection.Project(rows.Select(r => r with { Gate = r.Gate! with { Run = run } }).ToArray(), complete, run),
            GateAuditProjection.Project(stripped.Select(r => r with { Gate = r.Gate! with { Run = run } }).ToArray(), complete, run));
        Assert.Equal(b.Totals, a.Totals);
        Assert.Equal(b.IncompleteReasons, a.IncompleteReasons);
        Assert.Equal(b.Invalidated, a.Invalidated);
        var (withField, without) = (PhoneApprovalEvidence.Project(rows, complete, ["0123456789abcdef"]), PhoneApprovalEvidence.Project(stripped, complete, ["0123456789abcdef"]));
        Assert.Equal(without.Clients, withField.Clients);
        Assert.Equal((without.Missing, without.Complete), (withField.Missing, withField.Complete));
    }
}