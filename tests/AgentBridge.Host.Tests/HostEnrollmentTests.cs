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

    (HostEnrollment Host, EnrollmentApprover Approver, byte[] H) Exchanged(FakeTimeProvider time)
    {
        var host = new HostEnrollment(Unpinned(), Account, null, name: "H", time: time);
        Assert.Null(host.Describe(profile, World.Keys("H").Dh.Public).Refusal);
        var (a, h) = Join(host, time);
        return (host, a, h);
    }

    static (EnrollmentApprover Approver, byte[] H) Join(HostEnrollment host, FakeTimeProvider time)
    {
        var h = RandomNumberGenerator.GetBytes(32);
        DateTimeOffset Now() => time.GetUtcNow();
        var a = new EnrollmentApprover(Account, h, Ids.Device(World.Keys("H").Dh.Public), new(EnrollmentLockout.Counts.WrongEntries, Now), Now);
        var offer = a.OnCommit(host.Exchange(h, G, Person, "commit", []).Value!)!;
        Assert.True(a.OnReveal(host.Exchange(h, G, Person, "offer", offer).Value!));
        Assert.True(host.Shown.Expires > time.GetUtcNow());
        Assert.Equal("Dan's phone", host.Shown.Peer);
        Assert.Equal(CodeResult.Match, a.EnterCode1(host.Shown.Code!));
        return (a, h);
    }

    [WindowsFact("host enrollment runs on the Windows host")]
    public void A_matched_exchange_pins_the_approvers_genesis_once_its_host_add_is_in_the_chain()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (host, a, h) = Exchanged(time);
        using (host)
        {
            w.Genesis();
            Assert.Equal("not-matched", host.Exchange(h, G, Person, "genesis", w.Entry("G").Hash).Refusal);
            Assert.Equal("match", host.EnterCode2(a.Code2Shown!));
            Assert.Null(host.Exchange(h, G, Person, "genesis", w.Entry("G").Hash).Refusal);
            Assert.Equal("not-added", host.Finish(Relay(), PinPath, () => w.Now, _ => true));
            w.Add("+H", "G", "H", "G", kind: "host", transcript: a.TranscriptToSign);
            Assert.Equal("pinned", host.Finish(Relay("+H"), PinPath, () => w.Now, _ => true));
            Assert.Equal("verified", Unpinned().Update(Relay("+H"), Account).Outcome);
        }
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("not-started", "no-attempt")]
    [InlineData("wrong-code", "no-attempt")]
    [InlineData("expired", "no-attempt")]
    [InlineData("other-genesis", "genesis-mismatch")]
    [InlineData("second-channel", "no-genesis")]
    [InlineData("other-account", "chain-invalid")]
    public void A_refused_exchange_writes_no_pin(string mischief, string expected)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        if (mischief == "other-account") { w.AccountId = "other@example"; }
        w.Genesis();
        if (mischief == "not-started")
        {
            using var idle = new HostEnrollment(Unpinned(), Account, null, time: time);
            Assert.Equal("no-attempt", idle.Exchange(RandomNumberGenerator.GetBytes(32), G, Person, "commit", []).Refusal);
            Assert.Equal(expected, idle.Finish(Relay(), PinPath, () => w.Now, _ => true));
            Assert.False(File.Exists(PinPath));
            return;
        }
        var (host, a, h) = Exchanged(time);
        using var _ = host;
        if (mischief == "wrong-code")
        {
            var wrong = a.Code2Shown!.Replace(" ", "") == "000000" ? "111111" : "000000";
            Assert.Equal(["wrong", "wrong", "ended"], new[] { host.EnterCode2(wrong), host.EnterCode2(wrong), host.EnterCode2(wrong) });
        }
        else { Assert.Equal("match", host.EnterCode2(a.Code2Shown!)); }
        var other = RandomNumberGenerator.GetBytes(32);
        if (mischief == "second-channel") { Assert.Equal("attempt-bound", host.Exchange(other, G, Person, "genesis", w.Entry("G").Hash).Refusal); }
        else { host.Exchange(h, G, Person, "genesis", mischief == "other-genesis" ? other : w.Entry("G").Hash); }
        w.Add("+H", "G", "H", "G", kind: "host", transcript: a.TranscriptToSign);
        if (mischief == "expired") { time.Advance(EnrollmentLockout.AttemptLifetime); }
        Assert.Equal(expected, host.Finish(Relay("+H"), PinPath, () => w.Now, _ => true));
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
