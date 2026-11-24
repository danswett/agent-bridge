using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBridge.Host.Tests;

// #2971 (9B): retention and `audit archive` never move or delete a day holding rows of an ACTIVE gate run (a run id with rows and no
// gate.seal row; unreadable counts as active), so active-run evidence stays in the live files. Main's head model is unchanged.
public sealed class AuditActiveRunRetentionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-active-" + Guid.NewGuid().ToString("N"));
    readonly DateTimeOffset today = new(DateTime.UtcNow.Date, TimeSpan.Zero);
    static readonly Guid RunA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), RunB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    const int Old = -AgentAuditLog.KeepDays - 1, Older = -AgentAuditLog.KeepDays - 2;
    string Profile => Path.Combine(root, "selected");
    string Audit => Path.Combine(Profile, AgentAuditLog.FolderName);
    string Archive => Path.Combine(Audit, "archive");
    string Day(int offset) => Path.Combine(Audit, $"agents-{today.AddDays(offset):yyyy-MM-dd}.jsonl");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // One row per day, in order: the run it carries (Empty = none), whether it is that run's seal, and an optional unreadable gate.
    void Seed(params (int Day, Guid Run, bool Seal, bool Garbled)[] days) => Seed(false, days);

    // capital: the gate and its fields are written with other letter case, which the reader (Web defaults) still accepts.
    void Seed(bool capital, params (int Day, Guid Run, bool Seal, bool Garbled)[] days)
    {
        Directory.CreateDirectory(Audit);
        var previous = new string('0', 64);
        long sequence = 0;
        foreach (var (offset, run, seal, garbled) in days)
        {
            var operation = Guid.NewGuid();
            var header = JsonSerializer.Serialize(new { v = 1, previousSha256 = previous });
            object gate = garbled ? new { v = 1, run = "not-a-run" }
                : seal ? new GateAuditMetadata(1, operation, run, ++sequence, "control", "local", "person-client", operation, "none")
                : new GateAuditMetadata(1, operation, run, ++sequence, "intent", "local", "agent", operation, "none");
            var row = JsonSerializer.Serialize(new
            {
                v = 1, at = today.AddDays(offset), principal = "agent:fixture/session", action = seal ? "gate.seal" : "session.stop",
                outcome = seal ? "sealed" : "reserved", request = operation.ToString(), target = seal ? "profile" : operation.ToString(),
                previousSha256 = Hash(header), gate,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (capital) { row = row.Replace("\"gate\":{\"v\"", "\"Gate\":{\"v\"").Replace("\"run\":", "\"Run\":").Replace("\"phase\":", "\"Phase\":"); }
            File.WriteAllText(Day(offset), header + "\n" + row + "\n");
            previous = Hash(row);
        }
        Assert.True(new AgentAuditLog(Profile).Bootstrap());
    }

    (int Code, string Output, string Error) Archived()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = AuditCommands.Run(["audit", "archive", "--profile", "selected"], root, output, error);
        return (code, output.ToString(), error.ToString());
    }

    // The seal row a finished gate run leaves; the retention fixtures that are not about run evidence seal their run so it is not active.
    internal static AgentAuditEntry Seal(Guid run)
    {
        var id = Guid.NewGuid();
        return new("local", id.ToString(), "person:local", [], "gate.seal", "profile", "sealed",
            Gate: new GateAuditMetadata(1, id, run, 0, "control", "local", "person-client", id, "none"));
    }

    string Root() => JsonNode.Parse(File.ReadAllText(Path.Combine(Audit, "agents.head")))!["root"]!.GetValue<string>();

    [Fact]
    public void A_day_holding_rows_of_an_active_run_is_not_archived_and_the_reason_is_said()
    {
        Seed((Old, RunA, false, false), (-1, RunA, false, false), (0, Guid.Empty, false, false));
        var result = Archived();
        Assert.Equal(0, result.Code);
        Assert.Equal("Archived 0 audit files." + Environment.NewLine, result.Output);
        Assert.Contains("holds rows of an active gate run", result.Error);
        Assert.True(File.Exists(Day(Old)));
        Assert.False(Directory.Exists(Archive) && Directory.GetFiles(Archive).Length > 0);
        Assert.Equal(Path.GetFileName(Day(Old)), Root());
        Assert.True(new AgentAuditLog(Profile).Verify().Intact);
    }

    [Fact]
    public void Automatic_retention_never_deletes_a_day_holding_rows_of_an_active_run()
    {
        Seed((Old, RunA, false, false), (-1, Guid.Empty, false, false));
        Assert.True(new AgentAuditLog(Profile).Append(new AgentAuditEntry("local", Guid.NewGuid().ToString(), "agent:fixture/session", [], "agent.reply", "target", "delivered")));
        Assert.True(File.Exists(Day(Old)));
        Assert.Equal(Path.GetFileName(Day(Old)), Root());
    }

    [Fact]
    public void Two_overlapping_unsealed_runs_are_both_kept_and_only_days_before_them_move()
    {
        Seed((Older, Guid.Empty, false, false), (Old, RunA, false, false), (-1, RunB, false, false), (0, RunA, false, false));
        var result = Archived();
        Assert.Equal("Archived 1 audit files." + Environment.NewLine, result.Output);
        Assert.False(File.Exists(Day(Older)));
        Assert.True(File.Exists(Path.Combine(Archive, Path.GetFileName(Day(Older)))));
        Assert.True(File.Exists(Day(Old)) && File.Exists(Day(-1)));
        Assert.Equal(Path.GetFileName(Day(Old)), Root());
    }

    [Fact]
    public void A_row_whose_run_cannot_be_read_keeps_its_day()
    {
        Seed((Old, Guid.Empty, false, true), (-1, Guid.Empty, false, false));
        var result = Archived();
        Assert.Equal("Archived 0 audit files." + Environment.NewLine, result.Output);
        Assert.True(File.Exists(Day(Old)));
        Assert.Contains("holds rows of an active gate run", result.Error);
    }

    [Fact]
    public void A_gate_row_written_with_other_letter_case_still_holds_its_day()
    {
        Seed(true, (Old, RunA, false, false), (-1, Guid.Empty, false, false));
        Assert.Equal("Archived 0 audit files." + Environment.NewLine, Archived().Output);
        Assert.True(File.Exists(Day(Old)));
    }

    [Fact]
    public void A_sealed_runs_day_archives_normally_and_the_head_root_still_matches()
    {
        Seed((Older, RunA, false, false), (Old, RunA, true, false), (-1, Guid.Empty, false, false));
        var result = Archived();
        Assert.Equal(0, result.Code);
        Assert.Equal("Archived 2 audit files." + Environment.NewLine, result.Output);
        Assert.True(File.Exists(Path.Combine(Archive, Path.GetFileName(Day(Older)))));
        Assert.True(File.Exists(Path.Combine(Archive, Path.GetFileName(Day(Old)))));
        Assert.Equal(Path.GetFileName(Day(-1)), Root());
        Assert.True(new AgentAuditLog(Profile).Verify().Intact);
    }

    [Fact]
    public void Runs_unsealed_for_a_long_time_are_counted_and_shown_not_expired()
    {
        Seed((Older, RunA, false, false), (-1, RunB, false, false));
        var error = Archived().Error;
        Assert.Contains("2 active gate run(s), 1 abandoned", error);
        Assert.True(File.Exists(Day(Older)));
    }
}
