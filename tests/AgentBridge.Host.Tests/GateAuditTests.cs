using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class GateAuditTests : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string root = Path.Combine(Path.GetTempPath(), "gate-audit-" + Guid.NewGuid().ToString("N"));
    readonly Guid run = Guid.NewGuid(), target = Guid.NewGuid(), incarnation = Guid.NewGuid();
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
    string Profile => Path.Combine(root, "default");
    string Audit => Path.Combine(Profile, "audit");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    void Append(Guid operation, string phase = "intent", string outcome = "reserved", string action = "session.launch",
        Guid? session = null, Guid? question = null, bool accepted = true, bool gated = true)
    {
        var entry = JsonSerializer.Deserialize<AgentAuditEntry>(JsonSerializer.Serialize(new
        {
            direction = "local", request = operation, principal = "agent:machine/session", chain = Array.Empty<string>(),
            action, target = (session ?? target).ToString(), outcome, gate = gated ? new
            {
                v = 1, operation, run, sequence = 900, phase, path = "relay", actorKind = "agent",
                incarnation = session ?? incarnation, question, reasonCode = "none", prompt = "private gate prompt"
            } : null
        }), Json)!;
        Assert.Equal(accepted, new AgentAuditLog(Profile, clock).Append(entry, "private answer"));
    }

    GateAuditReport Read() => Assert.IsType<GateAuditReport>(GateAuditProjection.Read(Profile, run));

    [Fact]
    public void Gate_metadata_round_trips_through_the_writer_and_existing_owner_reader_without_text()
    {
        var operations = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < operations.Length; i++) { Append(operations[i], action: i == 1 ? "session.stop" : "session.launch"); }
        var output = new StringWriter();
        Assert.Equal(0, AuditCommands.Run(["audit"], root, output, new StringWriter()));
        var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonDocument.Parse(s)).ToArray();
        try
        {
            for (var i = 0; i < operations.Length; i++)
            {
                var gate = rows.Single(d => d.RootElement.GetProperty("gate").GetProperty("operation").GetGuid() == operations[i]).RootElement.GetProperty("gate");
                Assert.Equal(i + 1, gate.GetProperty("sequence").GetInt64());
            }
            foreach (var row in rows)
            {
                var gate = row.RootElement.GetProperty("gate");
                Assert.Equal(run, gate.GetProperty("run").GetGuid());
                Assert.Equal(incarnation, gate.GetProperty("incarnation").GetGuid());
                Assert.False(gate.TryGetProperty("prompt", out _));
                Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("dataSha256").ValueKind);
            }
            Assert.DoesNotContain("private", output.ToString() + string.Concat(Directory.GetFiles(Audit).Select(File.ReadAllText)));
        }
        finally { foreach (var row in rows) { row.Dispose(); } }
    }

    [Theory]
    [InlineData("attempted"), InlineData("reserved"), InlineData("unconfirmed")]
    public void Projection_credits_one_confirmed_operation_not_attempts_reservations_or_duplicate_terminals(string pending)
    {
        var operation = Guid.NewGuid();
        Append(operation);
        Append(operation, "terminal", "started");
        Append(operation, "terminal", "started");
        var unresolved = Guid.NewGuid();
        var session = Guid.NewGuid();
        Append(unresolved, outcome: pending, session: session);
        Append(unresolved, "terminal", pending, session: session);
        var report = Read();
        Assert.Equal(1, Assert.Single(report.Totals).Successes);
        Assert.Contains("unresolved-intent", report.IncompleteReasons);
        Assert.False(report.Invalidated);
    }

    [Theory]
    [InlineData(null), InlineData("started"), InlineData("failed"), InlineData("refused")]
    public void Conflicting_terminals_invalidate_but_retries_only_credit_a_logical_identity_once(string? previousOutcome)
    {
        var operation = Guid.NewGuid();
        Append(operation);
        Append(operation, "terminal", previousOutcome ?? "started");
        if (previousOutcome is not null) { operation = Guid.NewGuid(); Append(operation); }
        Append(operation, "terminal", previousOutcome is null ? "failed" : "started");
        var report = Read();
        Assert.Equal(previousOutcome is null, report.Invalidated);
        Assert.Equal(previousOutcome is null ? 0 : 1, Assert.Single(report.Totals).Successes);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void Writer_recovers_after_a_pruned_prefix_or_a_mid_file_torn_fragment(bool prune)
    {
        Append(Guid.NewGuid(), action: "session.stop");
        clock.Advance(TimeSpan.FromDays(31));
        Append(Guid.NewGuid());
        Append(Guid.NewGuid());
        var file = Assert.Single(Directory.GetFiles(Audit, "agents-*.jsonl"));
        if (prune) { File.Delete(file); }
        else
        {
            var lines = File.ReadAllLines(file);
            File.WriteAllText(file, lines[0] + "\n{\"gate\":\n" + string.Join("\n", lines.Skip(1)) + "\n");
        }
        Append(Guid.NewGuid(), action: "session.stop");
        using var last = JsonDocument.Parse(File.ReadAllLines(Directory.GetFiles(Audit, "agents-*.jsonl").Order().Last())[^1]);
        Assert.Equal(4, last.RootElement.GetProperty("gate").GetProperty("sequence").GetInt64());
        Assert.Contains(prune ? "sequence-break" : "audit-incomplete", Read().IncompleteReasons);
    }

    [Theory]
    [InlineData("session.launch"), InlineData("session.stop")]
    public void Non_answer_question_ids_cannot_split_a_logical_identity(string action)
    {
        Append(Guid.NewGuid(), action: action, question: Guid.NewGuid(), accepted: false);
        Append(Guid.NewGuid(), action: action, question: Guid.NewGuid(), accepted: false);
        Assert.Empty(Directory.GetFiles(Audit, "*.jsonl"));
    }

    [Theory]
    [InlineData("version"), InlineData("tail"), InlineData("complete-tail"), InlineData("outcome")]
    public void Unknown_versions_outcomes_and_a_torn_tail_are_incomplete_without_changing_files(string fault)
    {
        var operation = Guid.NewGuid();
        Append(operation);
        Append(operation, "terminal", "started");
        Assert.Empty(Read().IncompleteReasons);
        var file = Assert.Single(Directory.GetFiles(Audit, "launches-*.jsonl"));
        if (fault == "tail") { File.AppendAllText(file, "{\"gate\":"); }
        else if (fault == "complete-tail") { File.WriteAllText(file, File.ReadAllText(file).TrimEnd('\n')); }
        else
        {
            var lines = File.ReadAllLines(file);
            var row = JsonNode.Parse(lines[^1])!;
            if (fault == "version") { row["gate"]!["v"] = 9; } else { row["outcome"] = "unknown"; }
            lines[^1] = row.ToJsonString();
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
        }
        var before = Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        Assert.NotEmpty(Read().IncompleteReasons);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
        if (fault == "complete-tail") { Append(Guid.NewGuid(), gated: false); Assert.Contains("audit-incomplete", Read().IncompleteReasons); }
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void Missing_evidence_is_incomplete_and_the_reader_creates_no_profile_lock_or_writer(bool empty)
    {
        if (empty) { Directory.CreateDirectory(Audit); }
        var report = Read();
        Assert.NotEmpty(report.IncompleteReasons);
        Assert.Empty(report.Totals);
        if (empty) { Assert.Empty(Directory.GetFiles(Profile, "*", SearchOption.AllDirectories)); }
        else { Assert.False(Directory.Exists(root)); }
    }
}
