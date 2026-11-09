using System.Security.AccessControl;
using System.Security.Principal;
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
        var log = Log(max: 700);
        for (var i = 0; i < 3; i++) { log.Append(Entry(), new string('x', 10)); clock.Advance(TimeSpan.FromDays(1)); }
        log.Append(Entry(outcome: new string('o', 2000)) with { Reason = new string('r', 2000) });

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

    [Fact]
    public void Every_text_field_and_the_chain_are_capped_so_one_event_cannot_fill_the_budget()
    {
        var big = new string('z', 100_000);
        Log().Append(new AgentAuditEntry(big, big, big, Enumerable.Repeat(big, 1000).ToArray(), big, big, big, big));
        var text = File.ReadAllLines(Directory.GetFiles(Audit)[0])[0];
        Assert.True(text.Length < 10_000);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(AgentAuditLog.MaxChain, doc.RootElement.GetProperty("chain").GetArrayLength());
    }

    [Fact]
    public void A_partial_last_line_from_a_crash_is_not_glued_to_the_next_event()
    {
        Directory.CreateDirectory(Audit);
        var file = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        File.WriteAllText(file, "{\"at\":\"torn");
        Assert.True(Log().Append(Entry()));

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[1]);
        Assert.Equal("send", doc.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public void A_clock_that_jumps_forward_does_not_age_out_the_history_and_a_file_30_days_old_goes()
    {
        var log = Log();
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(3650));
        log.Append(Entry());
        Assert.Equal(2, Directory.GetFiles(Audit).Length);

        var back = new FakeTimeProvider(new DateTimeOffset(2026, 12, 8, 12, 0, 0, TimeSpan.Zero));
        var fixedLog = new AgentAuditLog(profile, back, said.Add);
        fixedLog.Append(Entry());
        back.Advance(TimeSpan.FromDays(1));
        fixedLog.Append(Entry());
        Assert.DoesNotContain("agents-2026-11-08.jsonl", Directory.GetFiles(Audit).Select(Path.GetFileName));
    }

    [Fact]
    public void Size_pruning_stops_at_a_file_it_cannot_delete_and_does_not_take_newer_days()
    {
        var log = Log();
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(1));
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(1));
        var oldest = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        if (!OperatingSystem.IsWindows()) { return; }
        using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Log(max: 400).Append(Entry(outcome: new string('o', 250)));
        }
        Assert.Equal(3, Directory.GetFiles(Audit).Length);
    }

    [Fact]
    public void The_audit_file_is_owner_only_and_an_existing_loose_one_is_tightened()
    {
        Directory.CreateDirectory(Audit);
        var file = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        File.WriteAllText(file, "");
        if (OperatingSystem.IsWindows())
        {
            var loose = new FileInfo(file).GetAccessControl();
            loose.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(loose);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        Assert.True(Log().Append(Entry()));
        AssertOwnerOnly(file);

        var fresh = Path.Combine(Audit, "agents-2026-11-09.jsonl");
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Log().Append(Entry()));
        AssertOwnerOnly(fresh);
    }

    static void AssertOwnerOnly(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var security = new FileInfo(file).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(me, security.GetOwner(typeof(SecurityIdentifier)));
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(me, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    long OneLine()
    {
        Assert.True(Log().Append(Entry()));
        var file = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        var length = new FileInfo(file).Length;
        File.Delete(file);
        return length;
    }

    [Fact]
    public void The_days_file_may_reach_exactly_the_size_limit_and_not_one_byte_past()
    {
        var one = OneLine();
        var log = Log(max: one * 2);
        Assert.True(log.Append(Entry()));
        Assert.True(log.Append(Entry()));
        Assert.Equal(one * 2, new FileInfo(Path.Combine(Audit, "agents-2026-11-08.jsonl")).Length);
        Assert.False(log.Append(Entry()));
        Assert.Equal(one * 2, new FileInfo(Path.Combine(Audit, "agents-2026-11-08.jsonl")).Length);

        File.Delete(Path.Combine(Audit, "agents-2026-11-08.jsonl"));
        Assert.False(Log(max: one - 1).Append(Entry()));
    }

    [Fact]
    public void A_looping_agent_cannot_grow_the_days_file_past_the_limit()
    {
        var one = OneLine();
        var log = Log(max: one * 3);
        var written = Enumerable.Range(0, 50).Count(_ => log.Append(Entry()));
        Assert.Equal(3, written);
        Assert.True(Directory.GetFiles(Audit).Sum(f => new FileInfo(f).Length) <= one * 3);
    }
    [Fact]
    public void A_huge_future_dated_file_does_not_wipe_the_real_days()
    {
        var log = Log(max: 2000);
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(1));
        File.WriteAllBytes(Path.Combine(Audit, "agents-2031-01-01.jsonl"), new byte[5000]);
        Assert.True(log.Append(Entry()));
        var names = Directory.GetFiles(Audit).Select(Path.GetFileName).ToArray();
        Assert.Contains("agents-2026-11-08.jsonl", names);
        Assert.Contains("agents-2031-01-01.jsonl", names);
    }
}