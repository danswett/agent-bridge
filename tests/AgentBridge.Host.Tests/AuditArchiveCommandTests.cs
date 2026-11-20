using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AuditArchiveCommandTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-archive-" + Guid.NewGuid().ToString("N"));
    readonly DateTimeOffset today = new(DateTime.UtcNow.Date, TimeSpan.Zero);
    string Profile => Path.Combine(root, "selected");
    string Audit => Path.Combine(Profile, AgentAuditLog.FolderName);
    string Archive => Path.Combine(Audit, "archive");
    string Day(int offset, bool launch = false) =>
        Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-{today.AddDays(offset):yyyy-MM-dd}.jsonl");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    void Seed()
    {
        Directory.CreateDirectory(Audit);
        // Seed aged evidence directly: appending today's event would already run automatic retention.
        foreach (var launch in new[] { false, true })
        {
            var previous = new string('0', 64);
            long sequence = launch ? 4 : 0;
            foreach (var offset in new[] { -AgentAuditLog.KeepDays - 1, -AgentAuditLog.KeepDays, -1, 0 })
            {
                var operation = Guid.NewGuid();
                var header = JsonSerializer.Serialize(new { v = 1, previousSha256 = previous });
                var row = JsonSerializer.Serialize(new
                {
                    v = 1, at = today.AddDays(offset), principal = "agent:fixture/session",
                    action = launch ? "session.launch" : "session.stop", outcome = "reserved",
                    request = operation.ToString(), target = operation.ToString(), previousSha256 = Hash(header),
                    gate = new GateAuditMetadata(1, operation, operation, ++sequence, "intent", "local", "agent", operation, "none")
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                File.WriteAllText(Day(offset, launch), header + "\n" + row + "\n");
                previous = Hash(row);
            }
        }
        Assert.True(new AgentAuditLog(Profile).Bootstrap());
        Assert.True(new AgentAuditLog(Profile).Verify().Intact);
        Assert.True(new AgentAuditLog(Profile).Verify(launch: true).Intact);
    }

    (int Code, string Output, string Error) Run()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = AuditCommands.Run(["audit", "archive", "--profile", "selected"], root, output, error);
        return (code, output.ToString(), error.ToString());
    }

    Dictionary<string, byte[]> Snapshot() =>
        Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);

    [Fact]
    public void Archive_moves_only_expired_closed_agent_days_unchanged_and_retained_evidence_still_verifies()
    {
        Seed();
        var before = Snapshot();
        var result = Run();
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal("Archived 2 audit files." + Environment.NewLine, result.Output);
        foreach (var (file, bytes) in before)
        {
            var moved = file == Day(-AgentAuditLog.KeepDays - 1) || file == Day(-AgentAuditLog.KeepDays);
            if (moved) { Assert.False(File.Exists(file)); }
            if (!file.EndsWith("agents.head", StringComparison.Ordinal))
            { Assert.Equal(bytes, File.ReadAllBytes(moved ? Path.Combine(Archive, Path.GetFileName(file)) : file)); }
        }
        var head = JsonNode.Parse(File.ReadAllText(Path.Combine(Audit, "agents.head")))!;
        Assert.Equal(2, head["retainedThrough"]!.GetValue<long>());
        Assert.Equal(Path.GetFileName(Day(-1)), head["root"]!.GetValue<string>());
        Assert.True(new AgentAuditLog(Profile).Verify().Intact);
        Assert.True(new AgentAuditLog(Profile).Verify(launch: true).Intact);
        var again = Run();
        Assert.Equal(0, again.Code);
        Assert.Empty(again.Error);
        Assert.Equal("Archived 0 audit files." + Environment.NewLine, again.Output);
        Assert.False(Directory.Exists(Path.Combine(root, "default")));
    }

    [Theory]
    [InlineData(false, "chain"), InlineData(true, "chain")]
    [InlineData(false, "head"), InlineData(true, "head")]
    [InlineData(false, "root"), InlineData(true, "root")]
    [InlineData(false, "incomplete"), InlineData(true, "incomplete")]
    public void Archive_refuses_faults_in_either_stream_before_moving_or_rewriting_any_evidence(bool launch, string fault)
    {
        Seed();
        if (fault == "head") { File.Delete(Path.Combine(Audit, launch ? "launches.head" : "agents.head")); }
        else if (fault == "root") { File.Delete(Day(-AgentAuditLog.KeepDays - 1, launch)); }
        else if (fault == "incomplete") { File.WriteAllText(Day(0, launch), File.ReadAllText(Day(0, launch)).TrimEnd('\n')); }
        else
        {
            var file = Day(-AgentAuditLog.KeepDays, launch);
            File.WriteAllText(file, File.ReadAllText(file).Replace("fixture", "changed", StringComparison.Ordinal));
        }
        var before = Snapshot();
        var result = Run();
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(root, result.Error);
        Assert.False(Directory.Exists(Archive));
        Assert.Equal(before.Keys.Order(), Snapshot().Keys.Order());
        foreach (var (file, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
    }

    [Fact]
    public void Truncation_of_the_newest_retained_day_after_archiving_is_still_reported()
    {
        Seed();
        Assert.Equal(0, Run().Code);
        var file = Day(0);
        File.WriteAllText(file, File.ReadLines(file).First() + "\n");
        Assert.False(new AgentAuditLog(Profile).Verify().Intact);
        var error = new StringWriter();
        AuditCommands.Run(["audit", "--profile", "selected"], root, TextWriter.Null, error);
        Assert.Contains("coverage-break", error.ToString());
        var archived = Directory.GetFiles(Archive).ToDictionary(f => f, File.ReadAllBytes);
        Assert.NotEqual(0, Run().Code);
        foreach (var (path, bytes) in archived) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }
}
