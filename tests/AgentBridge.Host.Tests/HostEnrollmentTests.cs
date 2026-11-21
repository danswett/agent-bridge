using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static RosterFixture;

namespace AgentBridge.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class HostEnrollmentTests : IDisposable
{
    readonly string profile = Directory.CreateTempSubdirectory("host-enrollment-").FullName;
    readonly World w = new();
    public void Dispose() => Directory.Delete(profile, true);

    sealed class ProbeKey(Action sign) : IHostEnrollmentKey
    {
        public byte[] PublicPoint => World.Keys("G").Dh.Public;
        public byte[] Sign(byte[] message) { sign(); return [1]; }
        public void Dispose() { }
    }

    [WindowsFact("host enrollment uses Windows custody")]
    public async Task Custody_does_not_hold_the_reader_lock_and_rechecks_the_switch_before_publication()
    {
        var reader = Reader();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var enrollment = new HostEnrollment(reader, Account, Generation, _ => new ProbeKey(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }));
        var describe = Task.Run(() => enrollment.Describe(profile, World.Keys("H").Dh.Public));
        Task? disable = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            disable = Task.Run(() => reader.Enrollment(Account, Generation,
                _ =>
                {
                    var off = new RosterVerifier(Account, Generation, () => w.Now);
                    off.Offer(w.Entry("G"));
                    typeof(HostRosterReader).GetProperty(nameof(HostRosterReader.Verified))!.SetValue(reader, off);
                    return true;
                }, _ => false));
            await disable.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await describe.WaitAsync(TimeSpan.FromSeconds(10));
            if (disable is not null) { await disable.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Assert.Equal("no-switch", describe.Result.Refusal);
        Assert.Null(describe.Result.Descriptor);
        Assert.Equal("not-enrolled", enrollment.Status(null, w.Id("G")));
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("argument")]
    [InlineData("disposed")]
    [InlineData("platform")]
    [InlineData("security")]
    public void Custody_failures_are_content_free_refusals_and_leave_the_attempt_retryable(string failure)
    {
        Exception Failure() => failure switch
        {
            "io" => new IOException("private sentinel"),
            "access" => new UnauthorizedAccessException("private sentinel"),
            "argument" => new ArgumentException("private sentinel"),
            "disposed" => new ObjectDisposedException("private sentinel"),
            "platform" => new PlatformNotSupportedException("private sentinel"),
            _ => new System.Security.SecurityException("private sentinel"),
        };
        var opens = 0;
        using var enrollment = new HostEnrollment(Reader(), Account, Generation, _ =>
            ++opens == 1 ? throw Failure() : new ProbeKey(() => throw Failure()));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = enrollment.Describe(profile, World.Keys("H").Dh.Public);
            Assert.Equal("missing-key", result.Refusal);
            Assert.Null(result.Descriptor);
            Assert.Equal("not-enrolled", enrollment.Status(null, w.Id("G")));
        }
        Assert.Equal(2, opens);
    }

    [WindowsFact("host enrollment uses Windows custody")]
    public async Task Concurrent_descriptors_open_custody_once_per_instance()
    {
        var opens = 0;
        using var enrollment = new HostEnrollment(Reader(), Account, Generation, _ =>
        {
            Interlocked.Increment(ref opens);
            return new ProbeKey(() => { });
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => enrollment.Describe(profile, World.Keys("H").Dh.Public))));
        Assert.Equal(1, opens);
        Assert.Single(results, result => result.Descriptor is not null);
        Assert.Equal(7, results.Count(result => result.Refusal == "attempt-already-open"));
    }

    [WindowsFact("hostSign private-scalar regression uses disposable DPAPI custody")]
    public void Descriptor_status_signed_frame_and_command_never_expose_the_actual_private_scalar()
    {
        var reader = Reader();
        using (HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi)) { }
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(profile, "host-sign", "signing-key.dpapi")),
            Encoding.UTF8.GetBytes("agent-bridge-roster-signing-key-v1"), DataProtectionScope.CurrentUser);
        byte[]? scalar = null;
        try
        {
            using var imported = ECDsa.Create();
            imported.ImportPkcs8PrivateKey(clear, out _);
            scalar = imported.ExportParameters(true).D!;
            using var enrollment = new HostEnrollment(reader, Account, Generation);
            var descriptor = enrollment.Describe(profile, World.Keys("H").Dh.Public).Descriptor!;
            var device = JsonNode.Parse(descriptor)!;
            var point = Codec.FromBase64Url(device["hostSign"]!.GetValue<string>(), "hostSign");
            Assert.True(P256.Verify(point, HostKey.Binding(Account, Generation, World.Keys("H").Dh.Public),
                Codec.FromBase64Url(device["hostSignProof"]!.GetValue<string>(), "hostSignProof")));
            var logs = new List<string>();
            var control = new HostControl(new HostControlOptions
            {
                Profile = "fixture", Folder = profile, Version = "0.1.0", Running = new(false),
                Machine = new("machine", "Fixture", "windows", "0.1.0"),
                SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => { },
                Stop = () => { }, Restart = () => { },
            }, logs.Add);
            var status = JsonSerializer.SerializeToElement(control.Status(), AgentBridge.Protocol.ProtocolJson.Options);
            Assert.Equal(Codec.Base64Url(SHA256.HashData(point)), status.GetProperty("hostSigning").GetProperty("keyId").GetString());
            var frame = Fleet.FleetRecordSigning.SignExisting(JsonSerializer.SerializeToElement(new { seq = 1 }), profile);
            Assert.True(Fleet.FleetRecordSigning.Verify(frame, point));
            var output = new StringWriter(); var error = new StringWriter();
            Assert.Equal(0, HostSignCommands.Run(["host-sign", "init", "--profile", Path.GetFileName(profile)],
                Path.GetDirectoryName(profile)!, () => new(CallerKind.HostCli, "fixture"), false, output, error));
            Assert.Equal(Codec.Base64Url(SHA256.HashData(point)),
                JsonNode.Parse(output.ToString())!["keyId"]!.GetValue<string>());
            foreach (var text in new[] { descriptor, status.GetRawText(), frame.GetRawText(), output.ToString(), error.ToString(), string.Join("\n", logs) })
            {
                Assert.True(Encoding.UTF8.GetBytes(text).AsSpan().IndexOf(scalar) < 0, "private scalar leaked");
                foreach (var secret in new[] { Convert.ToBase64String(scalar), Codec.Base64Url(scalar), Convert.ToHexString(scalar), Convert.ToHexString(scalar).ToLowerInvariant(), Convert.ToBase64String(clear) })
                { Assert.False(text.Contains(secret, StringComparison.Ordinal), "private key leaked"); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(clear); if (scalar is not null) { CryptographicOperations.ZeroMemory(scalar); } }
    }

    HostRosterReader Reader(bool switched = true)
    {
        PrivateFolder.Ensure(profile);
        w.Genesis();
        File.WriteAllText(Path.Combine(profile, "pin.json"), JsonSerializer.Serialize(new
        { account = Account, generation = Generation, genesis = Codec.Base64Url(w.Entry("G").Hash), heads = new[] { Codec.Base64Url(w.Entry("G").Hash) } }));
        var reader = new HostRosterReader(Path.Combine(profile, "pin.json"), () => w.Now);
        reader.Update(new RelayRoster("observe", Generation, [w.Entry("G").Canonical()]), Account);
        // Only tests supply the future switch; the reader still starts from a real private pin.
        var verifier = new RosterVerifier(Account, Generation, () => w.Now) { AcceptHostSign = switched };
        verifier.Offer(w.Entry("G"));
        typeof(HostRosterReader).GetProperty(nameof(HostRosterReader.Verified))!.SetValue(reader, verifier);
        return reader;
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("account")]
    [InlineData("generation")]
    public void Refusal_precedes_any_custody_access_and_reveals_no_identity(string code)
    {
        var reader = Reader();
        using var enrollment = new HostEnrollment(reader,
            code == "account" ? "another-account" : Account, code == "generation" ? new string('f', 32) : Generation);
        var result = enrollment.Describe(profile, World.Keys("H").Dh.Public);
        Assert.Equal(code, result.Refusal);
        Assert.Null(result.Descriptor);
        Assert.False(Directory.Exists(Path.Combine(profile, "host-sign")));
        Assert.Equal("not-enrolled", enrollment.Status(null, w.Id("G")));
    }

    HostRosterReader Unpinned() => new(HostRosterPinWriter.PinPath(profile), () => w.Now);

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unpinned_or_switched_off_host_describes_a_v1_host_without_opening_custody(bool pinned)
    {
        using var enrollment = new HostEnrollment(pinned ? Reader(switched: false) : Unpinned(), Account, Generation,
            _ => throw new InvalidOperationException("custody opened"), "Desk");
        var (descriptor, refusal) = enrollment.Describe(profile, World.Keys("H").Dh.Public);
        Assert.Null(refusal);
        var device = JsonNode.Parse(descriptor!)!.AsObject();
        Assert.Equal(["id", "dh", "kind", "platform", "name"], device.Select(p => p.Key));
        Assert.Equal((w.Id("H"), "host", "windows", "Desk"), (device["id"]!.GetValue<string>(), device["kind"]!.GetValue<string>(),
            device["platform"]!.GetValue<string>(), device["name"]!.GetValue<string>()));
        Assert.Equal("pending", enrollment.Status(null, w.Id("G")));
        Assert.False(File.Exists(HostRosterPinWriter.PinPath(profile)));
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void One_attempt_is_open_per_profile_across_instances_until_it_expires()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reader = Unpinned();
        using var first = new HostEnrollment(reader, Account, null, name: "Desk", time: time);
        using var second = new HostEnrollment(reader, Account, null, name: "Desk", time: time);
        Assert.Null(first.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        Assert.Equal("attempt-already-open", second.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        Assert.Equal("attempt-already-open", first.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(HostRosterPinWriter.PinPath(profile))!, "enrollment.lock")));
        time.Advance(EnrollmentLockout.AttemptLifetime);
        Assert.Equal("not-enrolled", first.Status(null, w.Id("G")));
        Assert.Null(second.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        Assert.False(File.Exists(HostRosterPinWriter.PinPath(profile)));
    }

    // Brief 184b, ruling (b): the host is the joiner over the approver's Noise channel (handshake hash h).
    string PinPath => HostRosterPinWriter.PinPath(profile);
    static byte[] G => World.Keys("G").Dh.Public;
    RelayRoster Relay(params string[] labels) => new("off", w.GenerationId, [.. new[] { "G" }.Concat(labels).Select(l => w.Entry(l).Canonical())]);

    static PresenceEntry Asserted(byte[] key, string kind, string name) => new(Ids.Device(key), name, kind, DateTimeOffset.UnixEpoch);
    static PresenceEntry Person => Asserted(G, "phone", "Dan's phone");

    (HostEnrollment Host, EnrollmentApprover Approver, byte[] H) Exchanged(FakeTimeProvider time, string by = "G", HostRosterReader? reader = null)
    {
        var host = new HostEnrollment(reader ?? Unpinned(), Account, null, name: "H", time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var (a, h) = Join(host, time, by);
        return (host, a, h);
    }

    static (EnrollmentApprover Approver, byte[] H) Join(HostEnrollment host, FakeTimeProvider time, string by = "G")
    {
        var h = RandomNumberGenerator.GetBytes(32);
        var (key, name) = (World.Keys(by).Dh.Public, by == "G" ? "Dan's phone" : by);
        DateTimeOffset Now() => time.GetUtcNow();
        var a = new EnrollmentApprover(Account, h, Ids.Device(World.Keys("H").Dh.Public), new(EnrollmentLockout.Counts.WrongEntries, Now), Now);
        var offer = a.OnCommit(host.Exchange(h, key, Asserted(key, "phone", name), "commit", []).Value!)!;
        Assert.True(a.OnReveal(host.Exchange(h, key, Asserted(key, "phone", name), "offer", offer).Value!));
        Assert.True(host.Shown.Expires > time.GetUtcNow());
        Assert.Equal(name, host.Shown.Peer);
        Assert.Equal(CodeResult.Match, a.EnterCode1(host.Shown.Code!));
        return (a, h);
    }

    // Its expiry timer never fires, so Finish's own expiry check is what refuses.
    sealed class StalledTimers() : FakeTimeProvider(DateTimeOffset.UnixEpoch)
    {
        public Action? OnTimerDispose;
        public TimeSpan WallShift;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + WallShift;
        public override long GetTimestamp() => base.GetUtcNow().UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        sealed class TimerProbe(ITimer timer, Action disposing) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose() { try { disposing(); } finally { timer.Dispose(); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new TimerProbe(new FakeTimeProvider().CreateTimer(callback, state, dueTime, period), () => OnTimerDispose?.Invoke());
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_an_approver_accepted_earlier_by_the_hosts_verified_chain_can_finish_and_retry(bool authenticated)
    {
        var reader = Reader(switched: false);
        w.Add("+P", "G", "P", "G");
        if (authenticated) { Assert.Equal("verified", reader.Update(Relay("+P"), Account).Outcome); }
        w.Now += RosterVerifier.Probation - TimeSpan.FromHours(1);
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (host, a, h) = Exchanged(time, "P", reader);
        using (host)
        {
            var key = World.Keys("P").Dh.Public;
            Assert.Equal("match", host.EnterCode2(a.Code2Shown!));
            Assert.Null(host.Exchange(h, key, Asserted(key, "phone", "P"), "genesis", w.Entry("G").Hash).Refusal);
            w.Add("+H", "P", "H", "+P", kind: "host", transcript: a.TranscriptToSign);
            Assert.Equal("approver-probation", host.Finish(Relay("+P", "+H"), PinPath, () => w.Now, _ => true));
            w.Now += TimeSpan.FromHours(1);
            // The closed writer gate is not opened by enrollment; retrying with the same verified history must not reset probation.
            Assert.Equal(authenticated ? "genesis-not-v3" : "approver-probation", host.Finish(Relay("+P", "+H"), PinPath, () => w.Now));
            Assert.False(File.Exists(PinPath));
            Assert.Equal(authenticated ? "pinned" : "approver-probation", host.Finish(Relay("+P", "+H"), PinPath, () => w.Now, _ => true));
            Assert.Equal(authenticated, File.Exists(PinPath));
        }
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("commit")]
    [InlineData("offer")]
    [InlineData("genesis")]
    [InlineData("abandon")]
    public void Every_remote_step_refuses_expiry_even_when_the_timer_has_not_fired(string step)
    {
        var time = new StalledTimers();
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var h = RandomNumberGenerator.GetBytes(32);
        byte[] value = [];
        if (step != "commit")
        {
            var a = new EnrollmentApprover(Account, h, w.Id("H"), new(EnrollmentLockout.Counts.WrongEntries, time.GetUtcNow), time.GetUtcNow);
            value = a.OnCommit(host.Exchange(h, G, Person, "commit", []).Value!)!;
            if (step == "genesis")
            {
                Assert.True(a.OnReveal(host.Exchange(h, G, Person, "offer", value).Value!));
                Assert.Equal(CodeResult.Match, a.EnterCode1(host.Shown.Code!));
                Assert.Equal("match", host.EnterCode2(a.Code2Shown!));
                w.Genesis();
                value = w.Entry("G").Hash;
            }
        }
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("expired", host.Exchange(h, G, Person, step, value).Refusal);
        Assert.Null(host.Shown.Expires);
        Assert.Empty(host.Charged);
        Assert.False(File.Exists(AttemptLog));
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void Remote_expiry_uses_elapsed_time_not_forward_or_backward_wall_clock_changes()
    {
        var time = new StalledTimers();
        using var host = new HostEnrollment(Unpinned(), Account, null, time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var h = RandomNumberGenerator.GetBytes(32);
        time.WallShift = TimeSpan.FromDays(1);
        Assert.Null(host.Exchange(h, G, Person, "commit", []).Refusal);
        time.Advance(TimeSpan.FromMinutes(6));
        time.WallShift = -TimeSpan.FromDays(1);
        Assert.Equal("expired", host.Exchange(h, G, Person, "abandon", []).Refusal);
        Assert.Null(host.Shown.Expires);
        Assert.Empty(host.Charged);
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("correct")]
    [InlineData("wrong")]
    [InlineData("matched")]
    public void A_late_commit_never_extends_code_entry_past_the_advertised_host_deadline(string entry)
    {
        var time = new StalledTimers();
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var deadline = host.Shown.Expires;
        time.Advance(EnrollmentLockout.AttemptLifetime - TimeSpan.FromSeconds(1));
        var (a, _) = Join(host, time);
        Assert.Equal(deadline, host.Shown.Expires);
        var code = a.Code2Shown!;
        if (entry == "matched") { Assert.Equal("match", host.EnterCode2(code)); }
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("ended", host.EnterCode2(entry == "wrong" ? "invalid" : code));
        Assert.Empty(host.Charged);
        Assert.False(File.Exists(AttemptLog));
        Assert.Null(host.Shown.Expires);
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public async Task The_third_wrong_mark_ends_under_the_real_lock_before_a_concurrent_entry_can_charge()
    {
        var time = new StalledTimers();
        var (host, _, _) = Exchanged(time);
        using (host)
        {
            var sync = typeof(HostEnrollment).GetField("sync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(host)!;
            Assert.Equal("wrong", host.EnterCode2("invalid"));
            Assert.Equal("wrong", host.EnterCode2("invalid"));
            Task<string>? concurrent = null;
            time.OnTimerDispose = () =>
            {
                Assert.Equal(3, JsonSerializer.Deserialize<long[]>(File.ReadAllBytes(AttemptLog))!.Length);
                Assert.True(Monitor.IsEntered(sync), "ending must not release the mark's synchronization lock");
                concurrent = Task.Run(() => host.EnterCode2("invalid"));
            };
            Assert.Equal("ended", host.EnterCode2("invalid"));
            Assert.NotNull(concurrent);
            Assert.Equal("no-exchange", await concurrent.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(3, host.Charged.Count);
            Assert.Equal(3, JsonSerializer.Deserialize<long[]>(File.ReadAllBytes(AttemptLog))!.Length);
        }
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void A_matched_exchange_pins_the_approvers_genesis_once_its_host_add_is_in_the_chain()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reader = Unpinned();
        var (host, a, h) = Exchanged(time, reader: reader);
        using (host)
        {
            w.Genesis();
            Assert.Equal("not-matched", host.Exchange(h, G, Person, "genesis", w.Entry("G").Hash).Refusal);
            Assert.Equal("match", host.EnterCode2(a.Code2Shown!));
            Assert.Null(host.Exchange(h, G, Person, "genesis", w.Entry("G").Hash).Refusal);
            Assert.Equal("not-added", host.Finish(Relay(), PinPath, () => w.Now, _ => true));
            w.Add("+H", "G", "H", "G", kind: "host", transcript: a.TranscriptToSign);
            Assert.Equal("pinned", host.Finish(Relay("+H"), PinPath, () => w.Now, _ => true));
            Assert.NotNull(reader.Pinned(Account)); // brief 207: the retained reader holds the pin at once
            Assert.Equal("verified", Unpinned().Update(Relay("+H"), Account).Outcome);
        }
    }

    // Brief 207: a probationary or held approver (§4.4), an account replaced mid roster read, an expiry the timer missed.
    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("not-started", "no-attempt")]
    [InlineData("wrong-code", "no-attempt")]
    [InlineData("expired", "expired")]
    [InlineData("other-genesis", "genesis-mismatch")]
    [InlineData("second-channel", "no-genesis")]
    [InlineData("other-account", "chain-invalid")]
    [InlineData("probation", "approver-probation")]
    [InlineData("held", "approver-probation")]
    [InlineData("replaced", "no-attempt")]
    [InlineData("account-changed", "account-changed")]
    public async Task A_refused_exchange_writes_no_pin(string mischief, string expected)
    {
        var time = new StalledTimers();
        if (mischief == "other-account") { w.AccountId = "other@example"; }
        w.Genesis();
        w.Add("+P", "G", "P", "G"); w.Add("+Q", "P", "Q", "+P");
        if (mischief == "not-started")
        {
            using var idle = new HostEnrollment(Unpinned(), Account, null, time: time);
            Assert.Equal("no-attempt", idle.Exchange(RandomNumberGenerator.GetBytes(32), G, Person, "commit", []).Refusal);
            Assert.Equal(expected, idle.Finish(Relay(), PinPath, () => w.Now, _ => true));
            Assert.False(File.Exists(PinPath));
            return;
        }
        var by = mischief switch { "probation" => "P", "held" => "Q", _ => "G" };
        var (host, a, h) = Exchanged(time, by);
        using var _ = host;
        var (key, peer) = (World.Keys(by).Dh.Public, Asserted(World.Keys(by).Dh.Public, "phone", by));
        if (mischief == "wrong-code")
        {
            var wrong = a.Code2Shown!.Replace(" ", "") == "000000" ? "111111" : "000000";
            Assert.Equal(["wrong", "wrong", "ended"], new[] { host.EnterCode2(wrong), host.EnterCode2(wrong), host.EnterCode2(wrong) });
        }
        else { Assert.Equal("match", host.EnterCode2(a.Code2Shown!)); }
        var other = RandomNumberGenerator.GetBytes(32);
        if (mischief == "second-channel") { Assert.Equal("attempt-bound", host.Exchange(other, key, peer, "genesis", w.Entry("G").Hash).Refusal); }
        else { host.Exchange(h, key, peer, "genesis", mischief == "other-genesis" ? other : w.Entry("G").Hash); }
        w.Add("+H", by, "H", by == "G" ? "+Q" : "+" + by, kind: "host", transcript: a.TranscriptToSign);
        if (mischief == "held")
        {
            // The fixture must tell Held from OnProbation apart: with Q accepted long ago (as an authenticated earlier acceptance would
            // record) it is off probation, yet still held because its signer P is on probation.
            var fresh = new RosterVerifier(Account, w.GenerationId, () => w.Now);
            fresh.Offer(Relay("+P", "+Q", "+H").Entries.Select(e => Entry.Parse(Encoding.UTF8.GetBytes(e))));
            fresh.AdoptFirstAccepted(w.Entry("+Q").Hash, w.Now - TimeSpan.FromHours(100));
            Assert.False(fresh.OnProbation(fresh.State.ActiveAdd(w.Id("Q"))!.Value));
            Assert.True(fresh.OnProbation(fresh.State.ActiveAdd(w.Id("P"))!.Value));
            Assert.Contains(w.Id("Q"), fresh.Held);
        }
        if (mischief == "expired") { time.Advance(EnrollmentLockout.AttemptLifetime); }
        var (roster, current) = (new TaskCompletionSource<RelayRoster?>(), Account);
        var finishing = HostEnrollment.FinishAsync(host, () => roster.Task, PinPath, () => w.Now, () => current, _ => true);
        if (mischief is "replaced" or "account-changed") { current = "other@example"; }
        if (mischief == "replaced") { host.Dispose(); }
        roster.SetResult(Relay("+P", "+Q", "+H"));
        Assert.Equal(expected, await finishing);
        Assert.False(File.Exists(PinPath));
    }

    string AttemptLog => Path.Combine(Path.GetDirectoryName(PinPath)!, "enrollment-attempts.json");

    // Brief 198: only the person's wrong code 2 at this host is charged, across restarts; 3 an hour lock.
    [WindowsFact("host enrollment runs on the Windows host")]
    public void Person_typed_wrong_codes_count_across_restarts_and_three_in_an_hour_lock_until_the_lock_ends()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        foreach (var (minute, wrongs) in new[] { (0, 1), (1, 1), (55, 1) })
        {
            time.SetUtcNow(DateTimeOffset.UnixEpoch.AddMinutes(minute));
            var (host, a, _) = Exchanged(time);
            using (host)
            {
                var wrong = a.Code2Shown!.Replace(" ", "") == "000000" ? "111111" : "000000";
                for (var i = 0; i < wrongs; i++) { Assert.Equal("wrong", host.EnterCode2(wrong)); }
            }
        }
        Assert.Equal(3, JsonSerializer.Deserialize<long[]>(File.ReadAllBytes(AttemptLog))!.Length);
        using var later = new HostEnrollment(Unpinned(), Account, null, time: time);
        time.SetUtcNow(DateTimeOffset.UnixEpoch.AddMinutes(61));
        Assert.Equal("locked", later.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        time.SetUtcNow(DateTimeOffset.UnixEpoch.AddMinutes(55) + EnrollmentLockout.LockFor);
        Assert.Null(later.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        Assert.Equal(3, later.Charged.Count);
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("malformed-commit")]
    [InlineData("malformed-reveal")]
    [InlineData("bind")]
    [InlineData("abandon")]
    [InlineData("expiry")]
    [InlineData("abandon-reopen")]
    [InlineData("bad-commits")]
    public void Remote_only_sequences_never_charge_the_lockout_and_a_real_approver_then_pins(string sequence)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        w.Genesis();
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time);
        var mallory = World.Keys("X").Dh.Public;
        var asserted = Asserted(mallory, "phone", "Mallory");
        var hx = RandomNumberGenerator.GetBytes(32);
        string? Step(string step, byte[] value) => host.Exchange(hx, mallory, asserted, step, value).Refusal;
        for (var round = 0; round < 3; round++)
        {
            Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
            switch (sequence)
            {
                case "malformed-commit": Assert.Equal("invalid-value", Step("commit", [1, 2])); break;
                case "malformed-reveal": Assert.Null(Step("commit", [])); Assert.Equal("attempt-ended", Step("offer", [1])); break;
                case "bind":
                    Assert.Null(Step("commit", []));
                    Assert.Equal("Mallory", host.Shown.Peer);
                    Assert.Equal("attempt-bound", host.Exchange(RandomNumberGenerator.GetBytes(32), G, Person, "commit", []).Refusal);
                    break;
                case "abandon": Assert.Null(Step("commit", [])); Assert.Null(Step("abandon", [])); break;
                case "abandon-reopen": Assert.Null(Step("commit", [])); Assert.Null(Step("abandon", [])); Assert.Equal("no-attempt", Step("commit", [])); break;
                case "bad-commits":
                    var refusals = Enumerable.Range(0, 100).Select(_ => Step("commit", [9])).ToList();
                    Assert.Equal("invalid-value", refusals[0]);
                    Assert.All(refusals.Skip(1), r => Assert.Equal("throttled", r));
                    break;
            }
            time.Advance(EnrollmentLockout.AttemptLifetime);
            Assert.Null(host.Shown.Expires);
        }
        Assert.False(File.Exists(AttemptLog));
        Assert.Empty(host.Charged);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var (a, h) = Join(host, time);
        Assert.Equal("match", host.EnterCode2(a.Code2Shown!));
        Assert.Null(host.Exchange(h, G, Person, "genesis", w.Entry("G").Hash).Refusal);
        w.Add("+H", "G", "H", "G", kind: "host", transcript: a.TranscriptToSign);
        Assert.Equal("pinned", host.Finish(Relay("+H"), PinPath, () => w.Now, _ => true));
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void Enroll_steps_are_refused_from_any_peer_the_relay_does_not_assert_is_a_person_and_commits_are_throttled()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var h = RandomNumberGenerator.GetBytes(32);
        foreach (var asserted in new PresenceEntry?[] { null, Asserted(G, "host", "Desk"), Asserted(G, "test", "agent"), Asserted(G, "agent", "agent"),
            Asserted(World.Keys("X").Dh.Public, "phone", "Not G") })
        {
            Assert.Equal("not-person", host.Exchange(h, G, asserted, "commit", []).Refusal);
        }
        Assert.Null(host.Shown.Peer);
        Assert.Null(host.Exchange(h, G, Person, "commit", []).Refusal);
        var hy = RandomNumberGenerator.GetBytes(32);
        var other = World.Keys("Y").Dh.Public;
        Assert.Equal("attempt-bound", host.Exchange(hy, other, Asserted(other, "app", "Laptop"), "commit", []).Refusal);
        Assert.Equal("throttled", host.Exchange(hy, other, Asserted(other, "app", "Laptop"), "commit", []).Refusal);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("attempt-bound", host.Exchange(hy, other, Asserted(other, "app", "Laptop"), "commit", []).Refusal);
        Assert.Equal("attempt-bound", host.Exchange(hy, other, Asserted(other, "app", "Laptop"), "abandon", []).Refusal);
        Assert.Equal("Dan's phone", host.Shown.Peer);
    }

    static JsonElement Value(string value) => JsonDocument.Parse(JsonSerializer.Serialize(new { value })).RootElement;
    static string? Refusal(object reply) => (string?)reply.GetType().GetProperty("refusal")!.GetValue(reply);

    [WindowsFact("host enrollment runs on the Windows host")]
    public void Malformed_remote_commits_count_against_the_throttle_before_their_value_is_decoded()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var h = RandomNumberGenerator.GetBytes(32);
        var refusals = Enumerable.Range(0, 3).Select(_ => Refusal(HostEnrollment.Remote(host, h, G, Person, "enroll.commit", Value("!!not base64!!")))).ToList();
        Assert.Equal(["invalid-value", "throttled", "throttled"], refusals);
        Assert.Equal("throttled", Refusal(HostEnrollment.Remote(host, h, G, Person, "enroll.commit", Value(""))));
        Assert.Null(host.Shown.Peer);
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void An_oversized_remote_value_is_refused_without_decoding_it_and_still_counts_against_the_throttle()
    {
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var h = RandomNumberGenerator.GetBytes(32);
        var big = Value(new string('A', 4_000_000));
        var (peer, asserted) = (G, Person); // built outside the measurement: first use of a test key allocates
        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = HostEnrollment.Remote(host, h, peer, asserted, "enroll.commit", big);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
        Assert.Equal("invalid-value", Refusal(first));
        Assert.Equal("throttled", Refusal(HostEnrollment.Remote(host, h, peer, asserted, "enroll.commit", big)));
    }

    // Acknowledged marks are the lockout: they must be on disk, not only in the cache, before the rename publishes them.
    sealed class RecordingLog(string path, List<string> events) : FileStream(path, FileMode.CreateNew, FileAccess.Write)
    {
        public override void Flush(bool flushToDisk) { events.Add($"flush:{flushToDisk}"); base.Flush(flushToDisk); }
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void A_charged_mark_is_flushed_to_disk_before_the_attempt_log_is_renamed_into_place()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var events = new List<string>();
        using var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time,
            createLog: path => new RecordingLog(path, events), moveLog: (from, to) => { events.Add("move"); File.Move(from, to, overwrite: true); });
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var (a, _) = Join(host, time);
        var wrong = a.Code2Shown!.Replace(" ", "") == "000000" ? "111111" : "000000";
        Assert.Equal("wrong", host.EnterCode2(wrong));
        var (flushed, moved) = (events.IndexOf("flush:True"), events.IndexOf("move"));
        Assert.True(flushed >= 0 && moved > flushed, string.Join(",", events));
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public async Task The_live_roster_source_is_empty_until_a_connection_sets_it_and_again_once_it_is_cleared()
    {
        var source = new LiveRosterSource();
        Assert.Null(await source.Reader());
        w.Genesis();
        var roster = Relay();
        source.Set(() => Task.FromResult<RelayRoster?>(roster));
        Assert.Same(roster, await source.Reader());
        source.Clear();
        Assert.Null(await source.Reader());
        using var host = new HostEnrollment(Unpinned(), Account, null);
        Assert.Equal("no-roster", await HostEnrollment.FinishAsync(host, source.Reader, PinPath, () => w.Now, () => Account));
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public async Task Enrollment_ops_are_answered_only_by_the_local_app_surface()
    {
        using var enrollment = new HostEnrollment(Unpinned(), Account, null, name: "Desk");
        var control = new HostControl(new HostControlOptions
        {
            Profile = "fixture", Folder = profile, Version = "0.1.0", Running = new(false),
            Machine = new("machine", "Desk", "windows", "0.1.0"),
            SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => { },
            Stop = () => { }, Restart = () => { },
            EnrollDescribe = () => enrollment.Describe(profile, World.Keys("H").Dh.Public),
            EnrollStatus = () => enrollment.Status(null, ""),
            EnrollShown = () => enrollment.Shown,
            EnrollCode = typed => enrollment.EnterCode2(typed),
            EnrollFinish = () => Task.FromResult(enrollment.Finish(Relay(), PinPath, () => w.Now)),
        }, _ => { });
        var described = JsonSerializer.SerializeToElement(await control.HandleAsync(null!, AgentBridge.Local.HostOps.EnrollDescribe, default));
        Assert.Equal(w.Id("H"), described.GetProperty("device").GetProperty("id").GetString());
        var status = JsonSerializer.SerializeToElement(await control.HandleAsync(null!, AgentBridge.Local.HostOps.EnrollStatus, default));
        Assert.Equal("pending", status.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("code").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("peer").ValueKind);
        Assert.True(status.GetProperty("expires").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        var typed = JsonSerializer.SerializeToElement(await control.HandleAsync(null!, AgentBridge.Local.HostOps.EnrollCode,
            JsonSerializer.SerializeToElement(new { code = "000 000" })));
        Assert.Equal("no-exchange", typed.GetProperty("result").GetString());
        w.Genesis();
        var finished = JsonSerializer.SerializeToElement(await control.HandleAsync(null!, AgentBridge.Local.HostOps.EnrollFinish, default));
        Assert.Equal("not-matched", finished.GetProperty("state").GetString());
        var reads = (HashSet<string>)typeof(LocalApi).GetField("Reads", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        Assert.DoesNotContain(AgentBridge.Local.HostOps.EnrollDescribe, reads);
        Assert.DoesNotContain(AgentBridge.Local.HostOps.EnrollStatus, reads);
        Assert.DoesNotContain(AgentBridge.Local.HostOps.EnrollCode, reads);
        Assert.DoesNotContain(AgentBridge.Local.HostOps.EnrollFinish, reads);
    }

    [WindowsFact("host enrollment CLI refuses before Windows identity access")]
    public async Task Dormant_command_emits_only_the_fixed_refusal_even_with_an_invalid_identity_profile()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "enrollment-command-child" })
        { start.ArgumentList.Add(argument); }
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        Assert.Equal(3, child.ExitCode);
        Assert.True((await output).Length == 0 && (await error) == "no-switch" + Environment.NewLine, "unexpected enrollment output");
    }

    [WindowsFact("host enrollment uses Windows hostSign custody")]
    public void Descriptor_proof_round_trips_but_only_the_exact_active_approver_add_and_matched_exchange_enroll()
    {
        var reader = Reader();
        using var enrollment = new HostEnrollment(reader, Account, Generation);
        Assert.Equal("missing-key", enrollment.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        using var key = HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi);
        var result = enrollment.Describe(profile, World.Keys("H").Dh.Public);
        Assert.Null(result.Refusal);
        var device = JsonNode.Parse(result.Descriptor!)!;
        Assert.True(device.AsObject().Select(p => p.Key).ToHashSet().SetEquals(
            ["id", "dh", "kind", "platform", "name", "hostSign", "hostSignAlg", "hostSignProof"]));
        Assert.Equal("attempt-already-open", enrollment.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        Assert.Equal("pending", enrollment.Status(null, w.Id("G")));
        DateTimeOffset Now() => DateTimeOffset.UnixEpoch;
        var n = new EnrollmentJoiner(Account, new byte[32], new(EnrollmentLockout.Counts.EndedAttempts, Now), Now);
        var a = new EnrollmentApprover(Account, new byte[32], w.Id("H"), new(EnrollmentLockout.Counts.WrongEntries, Now), Now);
        Assert.True(a.OnReveal(n.OnOffer(a.OnCommit(n.Commit()!)!)!));
        Assert.Equal(CodeResult.Match, a.EnterCode1(n.Code1Shown!));
        w.Add("+H", "G", "H", "G", kind: "host", transcript: a.TranscriptToSign);
        var body = JsonNode.Parse(w["+H"].Body)!;
        body["v"] = 2; body["device"] = device;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        var add = Entry.Parse(Encoding.UTF8.GetBytes(Entry.Canonical(bytes, World.Keys("G").Sign.Sign(Entry.SignedBy(bytes)))));
        Assert.True(P256.Verify(key.PublicPoint, HostKey.Binding(Account, Generation, World.Keys("H").Dh.Public), add.Body.Device!.HostSignProof!));
        var off = new RosterVerifier(Account, Generation, () => w.Now);
        off.Offer(w.Entry("G"));
        Assert.Equal(Invalid.Version, Assert.Single(off.Offer(add)).Reason);
        Assert.Equal(Verdict.Accepted, Assert.Single(reader.Verified!.Offer(add)).Verdict);
        Assert.Equal("pending", enrollment.Status(n, w.Id("G")));
        Assert.Equal(CodeResult.Match, n.EnterCode2(a.Code2Shown!));
        Assert.Equal("pending", enrollment.Status(n, w.Id("H")));
        Assert.Equal("enrolled", enrollment.Status(n, w.Id("G")));
        body["by"] = w.Id("H"); body["seq"] = 2;
        body["prev"] = new JsonArray(Codec.Base64Url(add.Hash));
        var self = Encoding.UTF8.GetBytes(body.ToJsonString());
        Assert.Equal(Invalid.Signer, Assert.Single(reader.Verified.Offer(
            Entry.Parse(Encoding.UTF8.GetBytes(Entry.Canonical(self, key.Sign(Entry.SignedBy(self))))))).Reason);
        typeof(HostRosterReader).GetProperty(nameof(HostRosterReader.Verified))!.SetValue(reader, off);
        Assert.Equal("pending", enrollment.Status(n, w.Id("G")));
        var accepted = new RosterVerifier(Account, Generation, () => w.Now) { AcceptHostSign = true };
        accepted.Offer(w.Entry("G"), add);
        typeof(HostRosterReader).GetProperty(nameof(HostRosterReader.Verified))!.SetValue(reader, accepted);
        w.Remove("-H", "G", "+H", "+H");
        body = JsonNode.Parse(w["-H"].Body)!;
        body["prev"] = new JsonArray(Codec.Base64Url(add.Hash)); body["target"]!["add"] = Codec.Base64Url(add.Hash);
        bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        Assert.Equal(Verdict.Accepted, Assert.Single(accepted.Offer(
            Entry.Parse(Encoding.UTF8.GetBytes(Entry.Canonical(bytes, World.Keys("G").Sign.Sign(Entry.SignedBy(bytes))))))).Verdict);
        Assert.Equal("pending", enrollment.Status(n, w.Id("G")));
    }
}
