using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentAuditLogTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "audit-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
    readonly List<string> said = [];

    public void Dispose() { try { Directory.Delete(profile, true); } catch (IOException) { } }

    AgentAuditLog Log(long max = AgentAuditLog.MaxBytes) => new(profile, clock, said.Add, max);
    string Audit => Path.Combine(profile, "audit");

    static AgentAuditEntry Entry(string action = "send", string outcome = "ok") =>
        new("local", "req-1", "agent:m1/s1", ["agent:m1/s1"], action, "m2/s2", outcome, "fine");

    [Fact]
    public void An_event_is_one_json_line_in_the_days_file_with_its_principal_and_outcome()
    {
        var log = Log();
        Assert.True(log.Append(Entry()));
        log.Append(Entry("launch", "refused"));

        var lines = File.ReadAllLines(Path.Combine(Audit, "agents-2026-11-08.jsonl"));
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[1]);
        Assert.Equal("agent:m1/s1", doc.RootElement.GetProperty("principal").GetString());
        Assert.Equal("launch", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("refused", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Message_text_is_never_written_only_its_hash_and_length()
    {
        const string secret = "tangerine-ostrich-4471-do-not-log";
        Log().Append(Entry(), secret);

        var all = string.Concat(Directory.GetFiles(Audit).Select(File.ReadAllText));
        Assert.DoesNotContain(secret, all);
        Assert.Contains("\"textLength\":" + secret.Length, all);
        Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))).ToLowerInvariant(), all);
    }

    [Fact]
    public void Files_older_than_thirty_days_go_and_the_file_being_written_stays()
    {
        var log = Log();
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(29));
        log.Append(Entry());
        Assert.Equal(2, Directory.GetFiles(Audit).Length);

        clock.Advance(TimeSpan.FromDays(2));
        log.Append(Entry());
        var names = Directory.GetFiles(Audit).Select(Path.GetFileName).Order().ToArray();
        Assert.DoesNotContain("agents-2026-11-08.jsonl", names);
        Assert.Contains("agents-2026-12-07.jsonl", names);
        Assert.Contains("agents-2026-12-09.jsonl", names);
    }

    [Fact]
    public void Past_the_size_limit_the_oldest_files_go_first_and_never_the_current_one()
    {
        var log = Log(max: 1500);
        for (var i = 0; i < 3; i++) { log.Append(Entry(), new string('x', 10)); clock.Advance(TimeSpan.FromDays(1)); }
        log.Append(Entry(outcome: new string('o', 2000)));

        var names = Directory.GetFiles(Audit).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal("agents-2026-11-11.jsonl", Assert.Single(names));
    }

    [Fact]
    public void A_write_that_fails_is_said_once_and_never_thrown_and_a_later_success_is_said()
    {
        Directory.CreateDirectory(profile);
        File.WriteAllText(Audit, "a file where the folder should be");
        var log = Log();

        Assert.False(log.Append(Entry()));
        Assert.False(log.Append(Entry()));
        Assert.Single(said);

        File.Delete(Audit);
        Assert.True(log.Append(Entry()));
        Assert.Equal(2, said.Count);
        Assert.Contains("writing again", said[1]);
    }

    [Fact]
    public void A_malformed_profile_or_a_missing_reason_never_throws_to_the_caller()
    {
        Assert.False(new AgentAuditLog("bad\0path", clock, said.Add).Append(Entry()));
        Assert.True(Log().Append(Entry() with { Reason = null! }));
    }

    [Fact]
    public void A_long_reason_is_cut()
    {
        Log().Append(Entry() with { Reason = new string('r', 5000) });
        using var doc = JsonDocument.Parse(File.ReadAllLines(Directory.GetFiles(Audit)[0])[0]);
        Assert.Equal(AgentAuditLog.MaxReason, doc.RootElement.GetProperty("reason").GetString()!.Length);
    }
}
