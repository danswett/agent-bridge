using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Host.Fleet;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class HostRosterPinTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "roster-pin-" + Guid.NewGuid().ToString("N"));
    string PathName => Path.Combine(folder, "roster-pin.json");
    readonly World w = new();

    public HostRosterPinTests()
    {
        PrivateFolder.Ensure(folder);
        w.Genesis();
        w.Add("B", "G", "B", "G");
    }

    RelayRoster Relay(params string[] labels) => new("observe", w.GenerationId, [.. labels.Select(l => w.Entry(l).Canonical())]);
    void Pin(string? account = null, string? generation = null, string? genesis = null, params string[] heads) =>
        Write(JsonSerializer.Serialize(new
        {
            account = account ?? RosterFixture.Account, generation = generation ?? RosterFixture.Generation,
            genesis = genesis ?? Codec.Base64Url(w.Entry("G").Hash),
            heads = heads.Length == 0 ? [Codec.Base64Url(w.Entry("B").Hash)] : heads,
        }));
    void Write(string text, string? path = null)
    {
        using var stream = PrivateFolder.CreateNew(path ?? PathName);
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.Flush(flushToDisk: true);
    }

    [Fact]
    public void Missing_pin_never_creates_trust_or_a_file()
    {
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Contains("unpinned", reader.Update(Relay("G", "B"), RosterFixture.Account).Reasons);
        Assert.Null(reader.Verified);
        Assert.False(File.Exists(PathName));
    }

    [Fact]
    public void A_pin_written_while_serving_is_used_by_the_next_verify_without_a_restart()
    {
        var path = HostRosterPinWriter.PinPath(folder);
        try
        {
            var reader = new HostRosterReader(path, () => w.Now);
            Assert.Contains("unpinned", reader.Update(null, RosterFixture.Account).Reasons);
            Assert.Null(reader.Pinned(RosterFixture.Account));
            var typed = PersonGenesis.FromFingerprint(HostRosterPinWriter.Fingerprint(w.Entry("G").Hash));
            // The fixture genesis is v1; stand in K1's v3 (capSign) predicate as the writer's own tests do.
            Assert.Equal("pinned", HostRosterPinWriter.Write(path, typed, Relay("G", "B"), RosterFixture.Account, () => w.Now, _ => true));
            Assert.Equal("verified", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
            Assert.NotNull(reader.Verified);
            Assert.Same(reader.Verified, reader.Pinned(RosterFixture.Account));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public async Task A_pinned_roster_with_the_switch_off_neither_signs_nor_supplies_registry_authority()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("verified", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        try
        {
            using var key = HostSignKey.LoadOrCreate(folder, KeyStorage.Dpapi);
            var trust = new FleetTrust(reader, folder, () => RosterFixture.Account);
            Assert.Null(trust.VerifiedRoster());
            var publication = new FleetPublication(new FleetPublisherTests.Source(), "machine", share: true, sign: trust.Sign);
            Assert.True(await publication.PollAsync((record, ct) =>
            {
                Assert.False(record.TryGetProperty("sig", out _));
                return Task.CompletedTask;
            }));
            Assert.False(reader.Verified!.AcceptHostSign);
        }
        finally { Directory.Delete(Path.Combine(folder, "host-sign"), recursive: true); }
    }

    [Fact]
    public void Fleet_membership_comes_from_the_pinned_roster_whatever_the_host_sign_switch_says()
    {
        Assert.Null(new FleetTrust(new HostRosterReader(PathName, () => w.Now), folder, () => RosterFixture.Account).Membership());
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("verified", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        Assert.False(reader.Verified!.AcceptHostSign);
        Assert.Same(reader.Verified, new FleetTrust(reader, folder, () => RosterFixture.Account).Membership());
    }

    [Fact]
    public void A_pin_that_cannot_supply_verified_membership_refuses_fleet_reads_instead_of_passing_every_host()
    {
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        var before = new FleetTrust(reader, folder, () => RosterFixture.Account);
        Assert.Contains("refused", Assert.Throws<InvalidOperationException>(() => before.Membership()).Message);
        Assert.Equal("verified", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        Assert.Same(reader.Verified, before.Membership());
        Assert.Throws<InvalidOperationException>(() => new FleetTrust(reader, folder, () => "another-account").Membership());
        Assert.Throws<InvalidOperationException>(() => new FleetTrust(reader, folder, () => null).Membership());

        File.Delete(PathName);
        Write("{broken");
        var unreadable = new FleetTrust(new HostRosterReader(PathName, () => w.Now), folder, () => RosterFixture.Account);
        Assert.Throws<InvalidOperationException>(() => unreadable.Membership());
    }

    [Fact]
    public void Matching_pin_retains_the_verified_reader_without_changing_the_pin()
    {
        Pin();
        var before = File.ReadAllBytes(PathName);
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("verified", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        var verified = reader.Verified;
        w.Add("C", "G", "C", "B");
        Assert.Equal("verified", reader.Update(Relay("G", "B", "C"), RosterFixture.Account).Outcome);
        Assert.Same(verified, reader.Verified);
        Assert.True(reader.Verified!.Serving.ContainsKey(w.Id("C")));
        Assert.Equal("head-regress", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        Assert.True(reader.Verified.Serving.ContainsKey(w.Id("C")));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("generation")]
    [InlineData("genesis")]
    public void A_different_pin_identity_never_becomes_a_verified_reader(string field)
    {
        Pin(account: field == "account" ? "another-account" : null,
            generation: field == "generation" ? new string('f', 32) : null,
            genesis: field == "genesis" ? Codec.Base64Url(new byte[32]) : null);
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("pin-mismatch", reader.Update(Relay("G", "B"), RosterFixture.Account).Outcome);
        Assert.Null(reader.Verified);
    }

    [Fact]
    public void A_regression_or_failed_sync_keeps_the_last_verified_reader()
    {
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        reader.Update(Relay("G", "B"), RosterFixture.Account);
        var verified = reader.Verified;
        Assert.Equal("head-regress", reader.Update(Relay("G"), RosterFixture.Account).Outcome);
        Assert.Equal("no-roster", reader.Update(null, RosterFixture.Account).Outcome);
        Assert.Equal("pin-mismatch", reader.Update(Relay("G", "B"), "another-account").Outcome);
        Assert.Same(verified, reader.Verified);
        Assert.True(reader.Verified!.Serving.ContainsKey(w.Id("B")));
    }

    // security.roster on connect (#3450, brief 181); a completed `closed` stops after the first pass.
    async Task<List<string>> Connect(HostRosterReader reader, string mode, Func<RelayRoster?> relay, List<string>? log = null)
    {
        log ??= [];
        await new HostRosterObserve(reader, () => mode, () => w.Now, log.Add)
            .ConnectedAsync(_ => Task.FromResult(relay()), RosterFixture.Account, Task.CompletedTask, default);
        return log;
    }

    [Fact]
    public async Task Off_never_feeds_the_pinned_reader_and_logs_todays_report()
    {
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        var log = await Connect(reader, "off", () => Relay("G", "B"));
        Assert.Null(reader.Verified);
        Assert.Throws<InvalidOperationException>(() => reader.Pinned(RosterFixture.Account));
        Assert.Equal("device roster (security.roster off): " + HostRoster.Verify(Relay("G", "B"), RosterFixture.Account, () => w.Now).Summary, Assert.Single(log));
    }

    [Fact]
    public async Task Observe_feeds_a_matching_pinned_chain_so_pinned_lists_the_serving_host_and_the_pin_is_unchanged()
    {
        Pin();
        var before = File.ReadAllBytes(PathName);
        var reader = new HostRosterReader(PathName, () => w.Now);
        var log = await Connect(reader, "observe", () => Relay("G", "B"));
        Assert.True(reader.Pinned(RosterFixture.Account)!.Serving.ContainsKey(w.Id("B")));
        Assert.Equal("device roster (security.roster observe): verified", Assert.Single(log));
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Fact]
    public async Task Observe_without_a_pin_keeps_the_unpinned_answer_and_creates_no_pin()
    {
        var reader = new HostRosterReader(PathName, () => w.Now);
        var log = await Connect(reader, "observe", () => Relay("G", "B"));
        Assert.Null(reader.Pinned(RosterFixture.Account));
        Assert.Null(reader.Verified);
        Assert.False(File.Exists(PathName));
        Assert.Equal("device roster (security.roster observe): unpinned", Assert.Single(log));
    }

    [Fact]
    public async Task Observe_refuses_an_unverifiable_snapshot_or_a_failed_fetch()
    {
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        var log = await Connect(reader, "observe", () => new RelayRoster("observe", w.GenerationId, ["not an entry"]));
        await Connect(reader, "observe", () => throw new IOException("relay connection ended"), log);
        Assert.Throws<InvalidOperationException>(() => reader.Pinned(RosterFixture.Account));
        Assert.Equal(["device roster (security.roster observe): pin-mismatch", "device roster (security.roster observe): unavailable"], log);
    }

    [Fact]
    public async Task Observe_refuses_a_relay_serving_another_accounts_roster()
    {
        Pin();
        var other = new World { AccountId = "someone-else@example" };
        other.Genesis();
        var reader = new HostRosterReader(PathName, () => w.Now);
        var log = await Connect(reader, "observe", () => new RelayRoster("observe", other.GenerationId, [other.Entry("G").Canonical()]));
        Assert.Throws<InvalidOperationException>(() => reader.Pinned(RosterFixture.Account));
        Assert.Equal("device roster (security.roster observe): pin-mismatch", Assert.Single(log));
    }

    [Fact]
    public async Task Observe_logs_one_line_per_state_change_and_no_roster_contents()
    {
        Pin();
        var reader = new HostRosterReader(PathName, () => w.Now);
        var log = new List<string>();
        var observe = new HostRosterObserve(reader, () => "observe", () => w.Now, log.Add);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await observe.ConnectedAsync(_ => Task.FromResult<RelayRoster?>(Relay("G", "B")), RosterFixture.Account, Task.CompletedTask, default);
        }
        Assert.Equal("device roster (security.roster observe): verified", Assert.Single(log));
        Assert.DoesNotContain(log, l => l.Contains(Convert.ToHexString(w.Entry("B").Hash)[..12], StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, 0.5, 5.0)]
    [InlineData(0, 0.0, 4.0)]
    [InlineData(0, 1.0, 6.0)]
    [InlineData(1, 0.5, 10.0)]
    [InlineData(9, 0.5, 40.0)]
    public void Observe_refetches_about_every_five_minutes_with_jitter_and_backs_off_on_failure(int failures, double random, double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), HostRosterObserve.Next(failures, random));

    [Theory]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Observe_caps_positive_jitter_at_forty_minutes_and_keeps_the_floor(int failures)
    {
        Assert.InRange(HostRosterObserve.Next(failures, 0.99), TimeSpan.FromMinutes(32), TimeSpan.FromMinutes(40));
        Assert.Equal(TimeSpan.FromMinutes(32), HostRosterObserve.Next(failures, 0));
        Assert.Equal(TimeSpan.FromMinutes(4), HostRosterObserve.Next(0, 0));
    }

    sealed class ObserveClock : FakeTimeProvider
    {
        readonly Channel<TimeSpan> waits = Channel.CreateUnbounded<TimeSpan>();
        int active;
        public int Active => Volatile.Read(ref active);
        public Task<TimeSpan> Waiting() => waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref active);
            waits.Writer.TryWrite(dueTime);
            return new Tracked(timer, () => Interlocked.Decrement(ref active));
        }
        sealed class Tracked(ITimer timer, Action disposed) : ITimer
        {
            int done;
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose()
            {
                timer.Dispose();
                if (Interlocked.Exchange(ref done, 1) == 0) { disposed(); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    HostRosterObserve Observer(HostRosterReader reader, ObserveClock time, List<string>? log = null) =>
        new(reader, () => "observe", () => time.GetElapsedTime(0), (log ?? []).Add, time, () => 0.5);

    [Fact]
    public async Task Observe_unpinned_passes_keep_the_base_delay()
    {
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var reader = new HostRosterReader(PathName, () => time.GetElapsedTime(0));
        var running = Observer(reader, time).ConnectedAsync(_ => Task.FromResult<RelayRoster?>(Relay("G", "B")),
            RosterFixture.Account, new TaskCompletionSource().Task, cancel.Token);
        try
        {
            for (var pass = 0; pass < 5; pass++)
            {
                var wait = await time.Waiting();
                Assert.Equal(TimeSpan.FromMinutes(5), wait);
                Assert.Null(reader.Pinned(RosterFixture.Account));
                time.Advance(wait);
            }
        }
        finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Observe_a_pin_appearing_after_unpinned_passes_verifies_within_the_base_delay()
    {
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var reader = new HostRosterReader(PathName, () => time.GetElapsedTime(0));
        var running = Observer(reader, time).ConnectedAsync(_ => Task.FromResult<RelayRoster?>(Relay("G", "B")),
            RosterFixture.Account, new TaskCompletionSource().Task, cancel.Token);
        try
        {
            for (var pass = 0; pass < 4; pass++) { time.Advance(await time.Waiting()); }
            var wait = await time.Waiting();
            Pin();
            Assert.Throws<InvalidOperationException>(() => reader.Pinned(RosterFixture.Account));
            Assert.Equal(TimeSpan.FromMinutes(5), wait);
            time.Advance(wait);
            await time.Waiting();
            Assert.NotNull(reader.Pinned(RosterFixture.Account));
        }
        finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Observe_second_fetch_waits_for_the_delay_and_updates_membership()
    {
        Pin();
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var reader = new HostRosterReader(PathName, () => time.GetElapsedTime(0));
        var fetches = 0;
        var running = Observer(reader, time).ConnectedAsync(_ =>
        {
            fetches++;
            return Task.FromResult<RelayRoster?>(fetches == 1 ? Relay("G", "B") : Relay("G", "B", "C"));
        }, RosterFixture.Account, new TaskCompletionSource().Task, cancel.Token);
        try
        {
            var wait = await time.Waiting();
            w.Add("C", "G", "C", "B");
            time.Advance(wait - TimeSpan.FromTicks(1));
            Assert.Equal(1, fetches);
            Assert.False(reader.Verified!.Serving.ContainsKey(w.Id("C")));
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Equal(TimeSpan.FromMinutes(5), await time.Waiting());
            Assert.Equal(2, fetches);
            Assert.True(reader.Verified.Serving.ContainsKey(w.Id("C")));
        }
        finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Observe_real_fetch_failures_grow_backoff_and_success_resets_it()
    {
        Pin();
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var fetches = 0;
        var running = Observer(new HostRosterReader(PathName, () => time.GetElapsedTime(0)), time).ConnectedAsync(_ =>
            ++fetches < 3 ? Task.FromException<RelayRoster?>(new IOException("fetch failed")) : Task.FromResult<RelayRoster?>(Relay("G", "B")),
            RosterFixture.Account, new TaskCompletionSource().Task, cancel.Token);
        try
        {
            Assert.Equal(TimeSpan.FromMinutes(10), await time.Waiting());
            time.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(TimeSpan.FromMinutes(20), await time.Waiting());
            time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(TimeSpan.FromMinutes(5), await time.Waiting());
        }
        finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Observe_close_stops_fetching_and_cancels_the_pending_delay() => await StopsObserve(close: true);

    [Fact]
    public async Task Observe_cancel_stops_fetching_and_cancels_the_pending_delay() => await StopsObserve(close: false);

    async Task StopsObserve(bool close)
    {
        Pin();
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var closed = new TaskCompletionSource();
        var fetches = 0;
        var running = Observer(new HostRosterReader(PathName, () => time.GetElapsedTime(0)), time).ConnectedAsync(_ =>
        {
            fetches++;
            return Task.FromResult<RelayRoster?>(Relay("G", "B"));
        }, RosterFixture.Account, closed.Task, cancel.Token);
        try
        {
            await time.Waiting();
            if (close) { closed.SetResult(); } else { cancel.Cancel(); }
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, time.Active);
            time.Advance(TimeSpan.FromHours(1));
            Assert.Equal(1, fetches);
        }
        finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Observe_disconnect_during_fetch_does_not_log_unavailable_or_poison_reconnect_state()
    {
        Pin();
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var closed = new TaskCompletionSource();
        var fetched = new TaskCompletionSource<RelayRoster?>();
        var log = new List<string>();
        var reader = new HostRosterReader(PathName, () => time.GetElapsedTime(0));
        var observe = Observer(reader, time, log);
        var running = observe.ConnectedAsync(_ => fetched.Task, RosterFixture.Account, closed.Task, cancel.Token);
        closed.SetResult();
        await observe.ConnectedAsync(_ => Task.FromResult<RelayRoster?>(Relay("G", "B")), RosterFixture.Account, Task.CompletedTask, default);
        fetched.SetException(new IOException("connection ended"));
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await observe.ConnectedAsync(_ => Task.FromResult<RelayRoster?>(Relay("G", "B")), RosterFixture.Account, Task.CompletedTask, default);
        Assert.Equal("device roster (security.roster observe): verified", Assert.Single(log));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Observe_close_or_cancel_stops_an_inflight_fetch_without_an_unavailable_log(bool close)
    {
        var time = new ObserveClock();
        using var cancel = new CancellationTokenSource();
        var closed = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var log = new List<string>();
        var running = Observer(new HostRosterReader(PathName, () => time.GetElapsedTime(0)), time, log)
            .ConnectedAsync(async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }, RosterFixture.Account, closed.Task, cancel.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (close) { closed.SetResult(); } else { cancel.Cancel(); }
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(log);
            Assert.Equal(0, time.Active);
        }
        finally { cancel.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void Repeated_sync_does_not_restart_the_verifiers_probation_clock()
    {
        Pin();
        w.Add("C", "B", "C", "B");
        var reader = new HostRosterReader(PathName, () => w.Now);
        reader.Update(Relay("G", "B", "C"), RosterFixture.Account);
        Assert.False(reader.Verified!.Serving.ContainsKey(w.Id("C")));
        w.Now = TimeSpan.FromHours(73);
        reader.Update(Relay("G", "B", "C"), RosterFixture.Account);
        Assert.True(reader.Verified.Serving.ContainsKey(w.Id("C")));
    }

    [Theory]
    [InlineData("{broken: secret-marker}")]
    [InlineData("{}")]
    [InlineData("{\"account\":\"a\",\"account\":\"b\"}")]
    [InlineData("[]")]
    public void Damaged_pin_reports_only_a_fixed_content_free_problem(string text)
    {
        Write(text);
        var reader = new HostRosterReader(PathName, () => w.Now);
        var check = reader.Update(Relay("G", "B"), RosterFixture.Account);
        Assert.Equal("pin-unreadable", check.Outcome);
        Assert.DoesNotContain("secret-marker", check.Summary);
        Assert.Null(reader.Verified);
    }

    [Fact]
    public void Oversize_or_locked_pin_is_not_treated_as_missing()
    {
        Write(new string(' ', 4097));
        Assert.Equal("pin-unreadable", new HostRosterReader(PathName, () => w.Now).Update(Relay("G"), RosterFixture.Account).Outcome);
        File.Delete(PathName);
        Pin();
        using var locked = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal("pin-unreadable", new HostRosterReader(PathName, () => w.Now).Update(Relay("G"), RosterFixture.Account).Outcome);
    }

    [Fact]
    public void A_valid_but_public_pin_is_not_trusted()
    {
        Pin();
        if (OperatingSystem.IsWindows())
        {
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
                System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(PathName).SetAccessControl(acl);
        }
        else { File.SetUnixFileMode(PathName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); }
        Assert.Equal("pin-unreadable", new HostRosterReader(PathName, () => w.Now).Update(Relay("G", "B"), RosterFixture.Account).Outcome);
    }

    string TransitionPath => Path.Combine(folder, "roster-transition.json");
    static readonly byte[] Nonce = new byte[32];
    CommandContext Context => new(w.Id("H"), CommandAction.AcceptHostSign, Instant.Parse("2026-11-04T12:00:00.123456789Z"), Nonce: Nonce);
    HostRosterReader PinnedReader()
    {
        if (!w.Labels.Contains("H")) { w.Add("H", "G", "H", "B", kind: "host"); Pin(); }
        var reader = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("verified", reader.Update(Relay("G", "B", "H"), RosterFixture.Account).Outcome);
        return reader;
    }
    PersonCommand Command(string by = "G", string request = "0f8fad5b-d9cb-469f-a165-70867728950e",
        string issued = "2026-11-04T11:59:00Z", string expires = "2026-11-04T12:04:00Z")
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, action = CommandAction.AcceptHostSign, request, by = w.Id(by), target = new { machine = w.Id("H") },
            nonce = Codec.Base64Url(Nonce), issuedAt = issued, expiresAt = expires, heads = new[] { Codec.Base64Url(w.Entry("H").Hash) },
        });
        return PersonCommand.Parse(Encoding.UTF8.GetBytes(PersonCommand.Canonical(body, World.Keys(by).Sign.Sign(PersonCommand.Signed(body)), null)));
    }
    JsonObject Snapshot() => JsonNode.Parse(File.ReadAllBytes(TransitionPath))!.AsObject();
    void ReplaceSnapshot(JsonObject snapshot) { File.Delete(TransitionPath); Write(snapshot.ToJsonString(), TransitionPath); }

    [Theory]
    [InlineData("G", "unsupported-verifier")]
    [InlineData("H", "signer")]
    [InlineData("B", "probation")]
    public void Transition_checks_refuse_without_writing_or_enabling_v2(string by, string reason)
    {
        var reader = PinnedReader();
        var before = File.ReadAllBytes(PathName);
        Assert.Equal(reason, reader.CheckAcceptHostSign(Command(by), Context));
        Assert.Equal(reason, reader.CheckAcceptHostSign(Command(by), Context));
        Assert.False(File.Exists(TransitionPath));
        Assert.Equal(before, File.ReadAllBytes(PathName));
        w.AddHostV2("v2", "G", "V2", "H");
        Assert.Contains("version", reader.Update(Relay("G", "B", "H", "v2"), RosterFixture.Account).Reasons);
        Assert.False(reader.Verified!.AcceptHostSign);
    }

    [Fact]
    public void The_internal_replay_record_survives_restart_but_never_authorizes_a_transition()
    {
        var reader = PinnedReader();
        var before = File.ReadAllBytes(PathName);
        Assert.Null(reader.RecordCommand(Command(), Context));
        Assert.Equal("2026-11-04T12:00:00.123456789Z", Snapshot()["highWater"]!.GetValue<string>());
        var restarted = PinnedReader();
        Assert.Equal("replay", restarted.RecordCommand(Command(), Context));
        Assert.Equal("unsupported-verifier", restarted.CheckAcceptHostSign(Command(), Context));
        Assert.False(restarted.Verified!.AcceptHostSign);
        Assert.Equal(before, File.ReadAllBytes(PathName));
    }

    [Theory]
    [InlineData(" ", "", false)]
    [InlineData("", " ", false)]
    [InlineData("\t", "\r\n", false)]
    [InlineData("", "", true)]
    public void A_noncanonical_replay_uuid_is_refused_after_restart_without_recording_the_command_again(string prefix, string suffix, bool uppercase)
    {
        var reader = PinnedReader();
        var command = Command();
        Assert.Null(reader.RecordCommand(command, Context));
        var snapshot = Snapshot();
        snapshot["requests"]![0]!["request"] =
            prefix + (uppercase ? command.Body.Request.ToUpperInvariant() : command.Body.Request) + suffix;
        ReplaceSnapshot(snapshot);
        var before = File.ReadAllBytes(TransitionPath);
        var restarted = PinnedReader();
        Assert.Equal("pin-unreadable", restarted.RecordCommand(command, Context));
        Assert.Equal("pin-unreadable", restarted.CheckAcceptHostSign(command, Context));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        snapshot["requests"]![0]!["request"] = command.Body.Request;
        ReplaceSnapshot(snapshot);
        var canonical = File.ReadAllBytes(TransitionPath);
        Assert.Equal("replay", restarted.RecordCommand(command, Context));
        Assert.Equal(canonical, File.ReadAllBytes(TransitionPath));
    }

    [Fact]
    public void Persisted_floor_and_high_water_apply_after_restart_and_clock_rollback()
    {
        var reader = PinnedReader();
        var later = Context with { Now = Instant.Parse("2026-11-04T12:03:00Z"), Floor = Instant.Parse("2026-11-04T12:00:00Z") };
        Assert.Null(reader.RecordCommand(Command(issued: "2026-11-04T12:01:00Z"), later));
        var restarted = PinnedReader();
        Assert.Equal("floor", restarted.RecordCommand(Command(request: "00000000-0000-0000-0000-000000000001"), Context));
        Assert.Equal("expired", restarted.RecordCommand(Command(issued: "2026-11-04T12:01:00Z", expires: "2026-11-04T12:02:00Z"), Context));
        Assert.Equal("2026-11-04T12:01:00Z", Snapshot()["highestIssuedAt"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("account", "pin-mismatch")]
    [InlineData("generation", "pin-mismatch")]
    [InlineData("genesis", "pin-mismatch")]
    [InlineData("v", "pin-unreadable")]
    [InlineData("acceptHostSign", "pin-unreadable")]
    [InlineData("extra", "pin-unreadable")]
    [InlineData("requests", "pin-unreadable")]
    [InlineData("heads", "pin-unreadable")]
    [InlineData("null-head", "pin-unreadable")]
    public void A_snapshot_never_crosses_identity_or_repairs_invalid_state(string field, string reason)
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var snapshot = Snapshot();
        snapshot[field == "null-head" ? "heads" : field] = field switch
        {
            "account" => JsonValue.Create("another-account"), "generation" => JsonValue.Create(new string('f', 32)),
            "genesis" => JsonValue.Create(Codec.Base64Url(new byte[32])), "v" => JsonValue.Create(3),
            "acceptHostSign" => JsonValue.Create(true), "requests" => new JsonArray(snapshot["requests"]![0]!.DeepClone(), snapshot["requests"]![0]!.DeepClone()),
            "heads" => new JsonArray(), "null-head" => new JsonArray((JsonNode?)null), _ => JsonValue.Create("secret-marker"),
        };
        ReplaceSnapshot(snapshot);
        var bytes = File.ReadAllBytes(TransitionPath);
        var restarted = new HostRosterReader(PathName, () => w.Now);
        Assert.Equal("verified", restarted.Update(Relay("G", "B", "H"), RosterFixture.Account).Outcome);
        Assert.Equal(reason, restarted.CheckAcceptHostSign(Command(), Context));
        Assert.Equal(reason, restarted.RecordCommand(Command(), Context));
        Assert.Equal(bytes, File.ReadAllBytes(TransitionPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_bad_or_deleted_transition_store_blocks_mutation_but_not_v1_sync(bool deleted)
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var verified = reader.Verified;
        var pin = File.ReadAllBytes(PathName);
        if (deleted) { File.Delete(TransitionPath); }
        else { File.WriteAllText(TransitionPath, "{broken: secret-marker}"); }
        w.Add("C", "G", "C", "H");
        Assert.Equal("verified", reader.Update(Relay("G", "B", "H", "C"), RosterFixture.Account).Outcome);
        Assert.Same(verified, reader.Verified);
        Assert.True(reader.Verified!.Serving.ContainsKey(w.Id("C")));
        Assert.Equal("pin-unreadable", reader.CheckAcceptHostSign(Command(), Context));
        Assert.Equal("pin-unreadable", reader.RecordCommand(Command(), Context));
        if (deleted) { Assert.False(File.Exists(TransitionPath)); }
        else { Assert.Equal("{broken: secret-marker}", File.ReadAllText(TransitionPath)); }
        Assert.Equal(pin, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void Cached_transition_heads_cannot_roll_back_to_an_older_verified_frontier()
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var accepted = Snapshot();
        var older = accepted.DeepClone().AsObject();
        older["heads"] = new JsonArray(Codec.Base64Url(w.Entry("B").Hash));
        ReplaceSnapshot(older);
        var bytes = File.ReadAllBytes(TransitionPath);
        Assert.Equal("head-regress", reader.CheckAcceptHostSign(Command(), Context));
        Assert.Equal("head-regress", reader.RecordCommand(Command(), Context));
        Assert.Equal(bytes, File.ReadAllBytes(TransitionPath));
        ReplaceSnapshot(accepted);
        Assert.Equal("unsupported-verifier", reader.CheckAcceptHostSign(Command(), Context));
    }

    [Fact]
    public void A_full_replay_store_prunes_only_after_expiry_plus_120_seconds_to_the_nanosecond()
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var snapshot = Snapshot();
        var replay = snapshot["requests"]![0]!;
        snapshot["requests"] = new JsonArray(Enumerable.Range(0, 512).Select(i =>
        {
            var item = replay.DeepClone(); item["request"] = $"00000000-0000-0000-0000-{i:x12}"; return item;
        }).ToArray());
        ReplaceSnapshot(snapshot);
        reader = PinnedReader();
        var command = Command(issued: "2026-11-04T12:05:00Z", expires: "2026-11-04T12:10:00Z");
        var boundary = Context with { Now = Instant.Parse("2026-11-04T12:06:00Z") };
        Assert.Equal("replay-full", reader.RecordCommand(command, boundary));
        Assert.Null(reader.RecordCommand(command, boundary with { Now = Instant.Parse("2026-11-04T12:06:00.000000001Z") }));
        Assert.Single(Snapshot()["requests"]!.AsArray());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("oversize")]
    [InlineData("surrogate")]
    public void Strict_snapshot_parsing_refuses_without_overwriting_the_file(string defect)
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var snapshot = Snapshot();
        if (defect == "missing") { snapshot.Remove("floor"); }
        var text = snapshot.ToJsonString();
        text = defect switch
        {
            "duplicate" => text.Replace("\"v\":2", "\"v\":2,\"v\":2"),
            "oversize" => text + new string(' ', 256 * 1024 + 1),
            "surrogate" => text.Replace(snapshot["highWater"]!.GetValue<string>(), "\\ud800"),
            _ => text,
        };
        File.Delete(TransitionPath);
        Write(text, TransitionPath);
        var restarted = PinnedReader();
        Assert.Equal("pin-unreadable", restarted.CheckAcceptHostSign(Command(), Context));
        Assert.Equal("pin-unreadable", restarted.RecordCommand(Command(), Context));
        Assert.Equal(text, File.ReadAllText(TransitionPath));
    }

    [Theory]
    [InlineData("highWater")]
    [InlineData("floor")]
    [InlineData("highestIssuedAt")]
    public void An_in_process_snapshot_cannot_regress_cached_clock_marks(string field)
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context with { Floor = Instant.Parse("2026-11-04T11:58:00Z") }));
        var snapshot = Snapshot();
        var command = Command();
        if (field == "highestIssuedAt")
        {
            // Prune the old record so schema/replay checks cannot mask the cached-mark regression.
            snapshot["highWater"] = "2026-11-04T12:06:00.000000001Z";
            snapshot["requests"] = new JsonArray();
            command = Command(issued: "2026-11-04T12:05:00Z", expires: "2026-11-04T12:10:00Z");
            ReplaceSnapshot(snapshot);
            Assert.Equal("unsupported-verifier", reader.CheckAcceptHostSign(command, Context));
        }
        snapshot[field] = "2026-11-04T11:57:00Z";
        ReplaceSnapshot(snapshot);
        Assert.Equal("pin-unreadable", reader.CheckAcceptHostSign(command, Context));
    }

    [Fact]
    public void A_failed_commit_preserves_the_snapshot_and_does_not_consume_the_request()
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var before = File.ReadAllBytes(TransitionPath);
        var next = Command(request: "00000000-0000-0000-0000-000000000001");
        reader.BeforeTransitionCommit = () => throw new IOException("secret-marker");
        Assert.Equal("transition-store", reader.RecordCommand(next, Context));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        Assert.Empty(Directory.GetFiles(folder, "roster-transition.json.*.tmp"));
        reader.BeforeTransitionCommit = null;
        Assert.Null(reader.RecordCommand(next, Context));
    }

    static CapConsumed Capability(int i, string expires = "2026-11-04T12:04:00Z") =>
        new($"00000000-0000-0000-0000-{i:x12}", Codec.Base64Url(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))),
            Codec.Base64Url(new byte[32]), "2026-11-04T11:59:00Z", expires);
    static CapEvidence Evidence => new(Codec.Base64Url(Encoding.UTF8.GetBytes("[ \n ]")),
        Codec.Base64Url(new byte[32]), Codec.Base64Url(new byte[32]), "2026-11-04T12:04:00Z");

    [Fact]
    public void V1_migrates_on_commit_and_v2_evidence_survives_without_enabling_support()
    {
        var reader = PinnedReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var old = Snapshot();
        old["v"] = 1;
        foreach (var name in new[] { "capHighestIssuedAt", "consumed", "evidence" }) { old.Remove(name); }
        ReplaceSnapshot(old);
        reader = PinnedReader();
        Assert.Null(reader.ReadEvidence());
        Assert.Equal(1, Snapshot()["v"]!.GetValue<int>());
        Assert.Null(reader.ConsumeCapability(Capability(1), Context.Now));
        Assert.Null(reader.StoreEvidence(Evidence));
        Assert.Equal(2, Snapshot()["v"]!.GetValue<int>());
        reader = PinnedReader();
        Assert.Equal(Evidence, reader.ReadEvidence());
        Assert.Equal("replay", reader.RecordCommand(Command(), Context));
        Assert.Null(reader.RecordCommand(Command(request: "00000000-0000-0000-0000-000000000099"), Context));
        Assert.Equal(Evidence, PinnedReader().ReadEvidence());
        Assert.Single(Snapshot()["consumed"]!.AsArray());
        Assert.Equal("unsupported-verifier", reader.CheckAcceptHostSign(Command(), Context));
        Assert.False(reader.Verified!.AcceptHostSign);
    }

    [Fact]
    public void Consumed_bound_replay_and_strict_pruning_survive_restart()
    {
        var reader = PinnedReader();
        for (var i = 0; i < 32; i++) { Assert.Null(reader.ConsumeCapability(Capability(i), Context.Now)); }
        var before = File.ReadAllBytes(TransitionPath);
        reader = PinnedReader();
        Assert.Equal("replay", reader.ConsumeCapability(Capability(0), Context.Now));
        Assert.Equal("replay", reader.ConsumeCapability(Capability(33) with { Nonce = Capability(0).Nonce }, Context.Now));
        Assert.Equal("consumed-full", reader.ConsumeCapability(Capability(33), Context.Now));
        var boundary = Instant.Parse("2026-11-04T12:06:00Z");
        Assert.Equal("consumed-full", reader.ConsumeCapability(Capability(33), boundary));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        Assert.Null(reader.ConsumeCapability(Capability(33, "2026-11-04T12:10:00Z") with { IssuedAt = "2026-11-04T12:05:00Z" },
            Instant.Parse("2026-11-04T12:06:00.000000001Z")));
        Assert.Single(Snapshot()["consumed"]!.AsArray());
    }

    [Fact]
    public void Capability_marks_ratchet_and_clock_reset_raises_floor_without_clearing_replay()
    {
        var reader = PinnedReader();
        Assert.Null(reader.ConsumeCapability(Capability(0), Context.Now));
        Assert.Null(reader.ConsumeCapability(Capability(1) with { IssuedAt = "2026-11-04T11:58:00Z" },
            Instant.Parse("2026-11-04T11:58:30Z")));
        Assert.Equal("2026-11-04T12:00:00.123456789Z", Snapshot()["highWater"]!.GetValue<string>());
        Assert.Equal(Instant.Parse("2026-11-04T11:59:00Z"), Instant.Parse(Snapshot()["floor"]!.GetValue<string>()));
        Assert.Equal("2026-11-04T11:59:00Z", Snapshot()["capHighestIssuedAt"]!.GetValue<string>());
        Assert.Equal(2, Snapshot()["consumed"]!.AsArray().Count);
        var changed = Snapshot(); changed["capHighestIssuedAt"] = null; ReplaceSnapshot(changed);
        Assert.Equal("pin-unreadable", reader.ConsumeCapability(Capability(2), Context.Now));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("accept")]
    [InlineData("identity")]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("downgrade")]
    [InlineData("count")]
    [InlineData("nonce")]
    public void Capability_snapshot_damage_is_never_repaired(string defect)
    {
        var reader = PinnedReader();
        Assert.Null(reader.ConsumeCapability(Capability(0), Context.Now));
        var snapshot = Snapshot();
        if (defect == "unknown") { snapshot["consumed"]![0]!["extra"] = true; }
        if (defect == "accept") { snapshot["acceptHostSign"] = true; }
        if (defect == "identity") { snapshot["generation"] = new string('f', 32); }
        if (defect == "future") { snapshot["v"] = 3; }
        if (defect == "downgrade") { snapshot["v"] = 1; }
        if (defect == "count") { snapshot["consumed"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 33).Select(i => Capability(i)), CamelCase); }
        if (defect == "nonce") { snapshot["consumed"]!.AsArray().Add(JsonSerializer.SerializeToNode(Capability(1) with { Nonce = Capability(0).Nonce }, CamelCase)); }
        if (defect == "missing") { snapshot.Remove("evidence"); }
        var text = snapshot.ToJsonString();
        if (defect == "duplicate") { text = text.Replace("\"v\":2", "\"v\":2,\"v\":2"); }
        File.Delete(TransitionPath); Write(text, TransitionPath);
        Assert.Equal(defect == "identity" ? "pin-mismatch" : "pin-unreadable",
            PinnedReader().ConsumeCapability(Capability(1), Context.Now));
        Assert.Equal(text, File.ReadAllText(TransitionPath));
    }

    [Fact]
    public void Evidence_size_and_atomic_write_failures_preserve_committed_state()
    {
        var reader = PinnedReader();
        Assert.Equal("transition-store", reader.StoreEvidence(Evidence));
        Assert.Null(reader.ConsumeCapability(Capability(0), Context.Now));
        var before = File.ReadAllBytes(TransitionPath);
        Assert.Equal("transition-store", reader.StoreEvidence(Evidence with { Collection = Codec.Base64Url(new byte[132097]) }));
        reader.BeforeTransitionCommit = () => throw new IOException("secret-marker");
        Assert.Equal("transition-store", reader.ConsumeCapability(Capability(1), Context.Now));
        Assert.Equal("transition-store", reader.StoreEvidence(Evidence));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        Assert.Empty(Directory.GetFiles(folder, "roster-transition.json.*.tmp"));
        reader.BeforeTransitionCommit = null;
        Assert.Null(reader.ConsumeCapability(Capability(1), Context.Now));
    }

    static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public void Dispose()
    {
        File.Delete(TransitionPath);
        File.Delete(TransitionPath + ".lock");
        File.Delete(PathName);
        Directory.Delete(folder);
    }
}
