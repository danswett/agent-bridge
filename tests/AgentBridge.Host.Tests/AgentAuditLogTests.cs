using System.Globalization;
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
    public void A_second_writer_is_refused_while_the_folder_lock_is_held_and_can_retry_after_release()
    {
        var second = Log();
        Assert.True(Log().Append(Entry()));
        var file = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        var before = File.ReadAllBytes(file);
        using (new FileStream(Path.Combine(profile, "audit.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(second.Append(Entry("lookup")));
            Assert.Equal(before, File.ReadAllBytes(file));
        }
        Assert.True(second.Append(Entry("lookup")));
        Assert.Equal(3, File.ReadAllLines(file).Length);
        Assert.Equal((true, false), Log().Verify());
    }
    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public void An_edited_or_missing_interior_day_breaks_verification_across_retained_days(bool launch, bool missing, bool tornHeader)
    {
        for (var day = 0; day < 3; day++)
        {
            if (day == 2 && tornHeader) { File.WriteAllText(Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-{clock.GetUtcNow():yyyy-MM-dd}.jsonl"), "{\"v\":"); }
            Assert.True(Log().Append(Entry(launch ? "session.launch" : "send")));
            clock.Advance(TimeSpan.FromDays(1));
        }
        var back = new AgentAuditLog(profile, new FakeTimeProvider(clock.GetUtcNow().AddDays(-2)), said.Add);
        Assert.False(back.Append(Entry(launch ? "session.launch" : "send")));
        Assert.Equal((true, false), Log().Verify(launch));
        Assert.True(Log().Append(Entry(launch ? "send" : "session.launch")));
        var middle = Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-2026-11-09.jsonl");
        if (missing) { File.Delete(middle); }
        else { File.WriteAllText(middle, File.ReadAllText(middle).Replace("\"fine\"", "\"evil\"")); }
        Assert.False(Log().Verify(launch).Intact);
        Assert.True(Log().Verify(!launch).Intact);
    }
    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public void A_deleted_header_or_record_in_header_position_breaks_verification(bool launch, bool later, bool replaced)
    {
        var action = launch ? "session.launch" : "send";
        Assert.True(Log().Append(Entry(action)));
        if (later)
        {
            clock.Advance(TimeSpan.FromDays(1));
            Assert.True(Log().Append(Entry(action)));
        }
        Assert.Equal((true, false), Log().Verify(launch));
        var file = Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-{clock.GetUtcNow():yyyy-MM-dd}.jsonl");
        var lines = File.ReadAllLines(file);
        using var header = JsonDocument.Parse(lines[0]);
        using var record = JsonDocument.Parse(lines[1]);
        // A forged first record keeps the prior-day link, isolating the header-position guard.
        var first = replaced ? lines[1].Replace(record.RootElement.GetProperty("previousSha256").GetString()!,
            header.RootElement.GetProperty("previousSha256").GetString()!) : lines[1];
        File.WriteAllText(file, first + "\n");
        Assert.False(Log().Verify(launch).Intact);
    }

    [Theory]
    [InlineData("{\"torn\":", true)]
    [InlineData("not-json", false)]
    public void Verification_reports_a_legacy_prefix_recovers_only_a_torn_JSON_prefix_and_refuses_newer_versions(string tail, bool recoverable)
    {
        Directory.CreateDirectory(Audit);
        var file = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        File.WriteAllText(file, "{\"action\":\"send\"}\n");
        Assert.True(Log().Append(Entry()));
        File.AppendAllText(file, tail);
        Assert.Equal((true, true), Log().Verify());
        Assert.True(Log().Append(Entry()));
        Assert.Equal((recoverable, true), Log().Verify());
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"v\":1", "\"v\":2"));
        Assert.False(Log().Verify().Intact);
    }
    [Fact]
    public void An_event_is_one_json_line_in_the_days_file_with_its_principal_and_outcome()
    {
        var log = Log();
        Assert.True(log.Append(Entry()));
        log.Append(Entry("launch", "refused"));

        var lines = File.ReadAllLines(Path.Combine(Audit, "agents-2026-11-08.jsonl")).Skip(1).ToArray();
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[1]);
        Assert.Equal("agent:m1/s1", doc.RootElement.GetProperty("principal").GetString());
        Assert.Equal("launch", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("refused", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void Launch_events_have_their_own_owner_only_daily_UTC_file()
    {
        var midnight = new MidnightClock();
        var log = new AgentAuditLog(profile, midnight, said.Add);
        Assert.True(log.Append(Entry("session.launch")));
        var file = Path.Combine(Audit, "launches-2026-11-08.jsonl");
        using var doc = JsonDocument.Parse(File.ReadAllLines(file)[^1]);
        Assert.Equal("session.launch", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("agent:m1/s1", doc.RootElement.GetProperty("principal").GetString());
        Assert.Equal(8, doc.RootElement.GetProperty("at").GetDateTimeOffset().Day);
        Assert.Equal(1, midnight.Reads);
        AssertOwnerOnly(file);
        Assert.False(File.Exists(Path.Combine(Audit, "agents-2026-11-08.jsonl")));

        Assert.True(log.Append(Entry("session.launch")));
        Assert.Equal(2, midnight.Reads);
        Assert.True(File.Exists(Path.Combine(Audit, "launches-2026-11-09.jsonl")));
        Assert.True(log.Append(Entry()));
        Assert.True(File.Exists(Path.Combine(Audit, "agents-2026-11-09.jsonl")));
    }

    sealed class MidnightClock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 11, 8, 23, 59, 59, TimeSpan.Zero).AddSeconds(2 * Reads++);
    }

    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public void A_complete_tail_recovers_in_the_current_day_or_refuses_an_unfinished_prior_header(bool launch, bool header, bool nextDay)
    {
        var action = launch ? "session.launch" : "send";
        var prefix = launch ? "launches" : "agents";
        Assert.True(Log().Append(Entry(action)));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Log().Append(Entry(action)));
        var file = Path.Combine(Audit, $"{prefix}-{clock.GetUtcNow():yyyy-MM-dd}.jsonl");
        var lines = File.ReadAllLines(file);
        File.WriteAllText(file, header ? lines[0] : string.Join("\n", lines));
        var before = File.ReadAllBytes(file);
        if (nextDay) { clock.Advance(TimeSpan.FromDays(1)); }
        var canRecover = !header || !nextDay;
        Assert.Equal(canRecover, Log().Append(Entry(action)));
        if (!canRecover)
        {
            Assert.Equal(before, File.ReadAllBytes(file));
            Assert.False(File.Exists(Path.Combine(Audit, $"{prefix}-{clock.GetUtcNow():yyyy-MM-dd}.jsonl")));
        }
        Assert.Equal((canRecover, false), Log().Verify(launch));
    }

    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public void Interrupted_torn_tail_recovery_cannot_extend_a_broken_day(bool launch, bool header, bool nextDay)
    {
        var action = launch ? "session.launch" : "send";
        Assert.True(Log().Append(Entry(action)));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Log().Append(Entry(action)));
        var file = Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-2026-11-09.jsonl");
        File.WriteAllText(file, (header ? "" : File.ReadAllText(file)) + "{\"v\":\n");
        var before = File.ReadAllBytes(file);
        if (nextDay) { clock.Advance(TimeSpan.FromDays(1)); }
        Assert.False(Log().Append(Entry(action)));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(File.Exists(Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-2026-11-10.jsonl")));
        Assert.Contains("could not write", Assert.Single(said));
    }

    [Theory]
    [InlineData("fa-IR", false), InlineData("fa-IR", true)]
    [InlineData("th-TH", false), InlineData("th-TH", true)]
    public void Daily_audit_names_remain_Gregorian_UTC_under_non_Gregorian_cultures(string culture, bool launch)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var localClock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero));
            var log = new AgentAuditLog(profile, localClock, said.Add);
            Assert.True(log.Append(Entry(launch ? "session.launch" : "send")));
            Assert.True(File.Exists(Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-2026-05-20.jsonl")));
            localClock.Advance(TimeSpan.FromDays(1));
            Assert.True(log.Append(Entry(launch ? "session.launch" : "send")));
            Assert.True(File.Exists(Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-2026-05-21.jsonl")));
            Assert.Equal((true, false), log.Verify(launch));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void An_unparseable_daily_name_is_reported_and_refused_without_writing(bool launch)
    {
        Assert.True(Log().Append(Entry(launch ? "session.launch" : "send")));
        var file = Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-2026-11-08.jsonl");
        var invalid = Path.Combine(Audit, $"{(launch ? "launches" : "agents")}-1405-02-31.jsonl");
        File.Move(file, invalid);
        var before = File.ReadAllBytes(invalid);
        Assert.False(Log().Verify(launch).Intact);
        Assert.False(Log().Append(Entry(launch ? "session.launch" : "send")));
        Assert.Equal(before, File.ReadAllBytes(invalid));
        Assert.False(File.Exists(file));
        Assert.Contains(said, message => message.Contains("could not be verified", StringComparison.Ordinal));
        Assert.Contains(said, message => message.Contains("could not write", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void The_launch_append_that_would_pass_16_MiB_is_refused_without_changing_the_file(bool torn, int over)
    {
        const long quota = 16L * 1024 * 1024;
        var log = Log();
        var entry = Entry("session.launch");
        const string prompt = "private launch text \u00e9";
        Assert.True(log.Append(entry, prompt));
        var file = Path.Combine(Audit, "launches-2026-11-08.jsonl");
        if (torn) { File.AppendAllText(file, "x"); Assert.True(log.Append(entry, prompt)); }
        var line = System.Text.Encoding.UTF8.GetBytes(File.ReadAllLines(file)[^1] + "\n");
        using (var seed = new FileStream(file, FileMode.Create, FileAccess.Write))
        {
            seed.Write(line);
            seed.SetLength(quota - line.Length - (torn ? 1 : 0) + over);
            seed.Position = seed.Length - line.Length - 1 - (torn ? 1 : 0);
            seed.Write("\n"u8);
            seed.Write(line);
            if (torn) { seed.WriteByte((byte)'x'); }
        }
        var before = new FileInfo(file).Length;
        Assert.Equal(over == 0, log.Append(entry, prompt));
        Assert.Equal(over == 0 ? quota : before, new FileInfo(file).Length);
        using (var read = File.OpenRead(file))
        {
            read.Position = before - 1;
            Assert.Equal(torn ? 'x' : '\n', read.ReadByte());
            if (over == 0)
            {
                if (torn) { Assert.Equal('\n', read.ReadByte()); }
                var appended = new byte[line.Length];
                read.ReadExactly(appended);
                using var expected = JsonDocument.Parse(line);
                using var actual = JsonDocument.Parse(appended);
                Assert.Equal(expected.RootElement.EnumerateObject().Where(p => p.Name != "previousSha256").Select(p => p.ToString()),
                    actual.RootElement.EnumerateObject().Where(p => p.Name != "previousSha256").Select(p => p.ToString()));
            }
        }
        Assert.False(log.Append(entry, prompt));
        Assert.Equal(over == 0 ? quota : before, new FileInfo(file).Length);
        var refusal = Assert.Single(said);
        Assert.DoesNotContain(prompt, refusal);
        Assert.DoesNotContain(profile, refusal);
    }

    [Fact]
    public void Earlier_launch_days_count_toward_the_quota_and_send_pruning_cannot_remove_them()
    {
        var log = Log(max: 700);
        Assert.True(log.Append(Entry("session.launch")));
        var earlier = Path.Combine(Audit, "launches-2026-11-08.jsonl");
        var line = System.Text.Encoding.UTF8.GetBytes(File.ReadAllLines(earlier)[^1] + "\n");
        using (var seed = new FileStream(earlier, FileMode.Create, FileAccess.Write))
        {
            seed.SetLength(16L * 1024 * 1024);
            seed.Position = seed.Length - line.Length - 1;
            seed.Write("\n"u8);
            seed.Write(line);
        }
        clock.Advance(TimeSpan.FromDays(1));
        Assert.False(new AgentAuditLog(profile, clock, said.Add).Append(Entry("session.launch")));
        Assert.True(log.Append(Entry()));
        clock.Advance(TimeSpan.FromDays(31));
        Assert.True(log.Append(Entry()));
        Assert.True(File.Exists(earlier));
        Assert.Equal(16L * 1024 * 1024, new FileInfo(earlier).Length);
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
        Assert.True(Log().Verify().Intact);
    }

    [Fact]
    public void Past_the_size_limit_the_oldest_files_go_first_and_never_the_current_one()
    {
        var log = Log(max: 1000);
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
        using var doc = JsonDocument.Parse(File.ReadAllLines(Directory.GetFiles(Audit)[0])[^1]);
        Assert.Equal(AgentAuditLog.MaxReason, doc.RootElement.GetProperty("reason").GetString()!.Length);
    }

    [Fact]
    public void Every_text_field_and_the_chain_are_capped_so_one_event_cannot_fill_the_budget()
    {
        var big = new string('z', 100_000);
        Log().Append(new AgentAuditEntry(big, big, big, Enumerable.Repeat(big, 1000).ToArray(), big, big, big, big));
        var text = File.ReadAllLines(Directory.GetFiles(Audit)[0])[^1];
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
        Assert.Equal(3, lines.Length);
        using var doc = JsonDocument.Parse(lines[2]);
        Assert.Equal("send", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal((true, false), Log().Verify());
    }

    [Fact]
    public void A_clock_that_jumps_forward_does_not_age_out_the_history_and_a_file_30_days_old_goes()
    {
        var log = Log();
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(3650));
        log.Append(Entry());
        Assert.Equal(2, Directory.GetFiles(Audit).Length);
        // Remove the future tail before exercising age pruning: backdated chain writes are refused.
        File.Delete(Path.Combine(Audit, $"agents-{clock.GetUtcNow():yyyy-MM-dd}.jsonl"));

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
            Assert.True(Log(max: 600).Append(Entry()));
            Assert.Contains("could not trim old files", Assert.Single(said));
            Assert.Equal(3, Directory.GetFiles(Audit).Length);
            Assert.All(Directory.GetFiles(Audit), file => Assert.True(new FileInfo(file).Length > 0));
        }
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

    long headerBytes;
    long OneLine()
    {
        Assert.True(Log().Append(Entry()));
        var file = Path.Combine(Audit, "agents-2026-11-08.jsonl");
        var length = new FileInfo(file).Length;
        headerBytes = System.Text.Encoding.UTF8.GetByteCount(File.ReadAllLines(file)[0] + "\n");
        File.Delete(file);
        return length - headerBytes;
    }

    [Fact]
    public void The_days_file_may_reach_exactly_the_size_limit_and_not_one_byte_past()
    {
        var one = OneLine();
        var log = Log(max: headerBytes + one * 2);
        Assert.True(log.Append(Entry()));
        Assert.True(log.Append(Entry()));
        Assert.Equal(headerBytes + one * 2, new FileInfo(Path.Combine(Audit, "agents-2026-11-08.jsonl")).Length);
        Assert.False(log.Append(Entry()));
        Assert.Equal(headerBytes + one * 2, new FileInfo(Path.Combine(Audit, "agents-2026-11-08.jsonl")).Length);

        File.Delete(Path.Combine(Audit, "agents-2026-11-08.jsonl"));
        Assert.False(Log(max: one - 1).Append(Entry()));
    }

    [Fact]
    public void A_looping_agent_cannot_grow_the_days_file_past_the_limit()
    {
        var one = OneLine();
        var log = Log(max: headerBytes + one * 3);
        var written = Enumerable.Range(0, 50).Count(_ => log.Append(Entry()));
        Assert.Equal(3, written);
        Assert.True(Directory.GetFiles(Audit).Sum(f => new FileInfo(f).Length) <= headerBytes + one * 3);
    }
    [Fact]
    public void A_huge_future_dated_file_does_not_wipe_the_real_days()
    {
        var log = Log(max: 2000);
        log.Append(Entry());
        clock.Advance(TimeSpan.FromDays(1));
        File.WriteAllBytes(Path.Combine(Audit, "agents-2031-01-01.jsonl"), new byte[5000]);
        Assert.False(log.Append(Entry()));
        var names = Directory.GetFiles(Audit).Select(Path.GetFileName).ToArray();
        Assert.Contains("agents-2026-11-08.jsonl", names);
        Assert.Contains("agents-2031-01-01.jsonl", names);
    }
}