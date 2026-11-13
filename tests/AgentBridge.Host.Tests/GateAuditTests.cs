using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class GateAuditTests : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string root = Path.Combine(Path.GetTempPath(), "gate-audit-" + Guid.NewGuid().ToString("N"));
    readonly Guid run = Guid.NewGuid(), target = Guid.NewGuid(), incarnation = Guid.NewGuid();
    string Profile => Path.Combine(root, "default");
    string Audit => Path.Combine(Profile, "audit");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    void Append(Guid operation, string phase = "intent", string outcome = "reserved", string action = "session.launch")
    {
        var entry = JsonSerializer.Deserialize<AgentAuditEntry>(JsonSerializer.Serialize(new
        {
            direction = "local", request = operation, principal = "agent:machine/session", chain = Array.Empty<string>(),
            action, target = target.ToString(), outcome, gate = new
            {
                v = 1, operation, run, sequence = 900, phase, path = "relay", actorKind = "agent",
                incarnation, question = (Guid?)null, reasonCode = "none", prompt = "private gate prompt"
            }
        }), Json)!;
        Assert.True(new AgentAuditLog(Profile).Append(entry, "private answer"));
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
        Append(unresolved, outcome: pending);
        var report = Read();
        Assert.Equal(1, Assert.Single(report.Totals).Successes);
        Assert.Contains("unresolved-intent", report.IncompleteReasons);
        Assert.False(report.Invalidated);
    }

    [Fact]
    public void Conflicting_terminals_invalidate_the_run()
    {
        var operation = Guid.NewGuid();
        Append(operation);
        Append(operation, "terminal", "started");
        Append(operation, "terminal", "failed");
        Assert.True(Read().Invalidated);
    }

    [Theory]
    [InlineData("version"), InlineData("tail"), InlineData("outcome")]
    public void Unknown_versions_outcomes_and_a_torn_tail_are_incomplete_without_changing_files(string fault)
    {
        var operation = Guid.NewGuid();
        Append(operation);
        Append(operation, "terminal", "started");
        Assert.Empty(Read().IncompleteReasons);
        var file = Assert.Single(Directory.GetFiles(Audit, "launches-*.jsonl"));
        if (fault == "tail") { File.AppendAllText(file, "{\"gate\":"); }
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
