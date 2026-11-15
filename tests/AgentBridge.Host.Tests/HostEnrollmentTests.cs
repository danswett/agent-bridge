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
using Xunit;
using static RosterFixture;

namespace AgentBridge.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class HostEnrollmentTests : IDisposable
{
    readonly string profile = Directory.CreateTempSubdirectory("host-enrollment-").FullName;
    readonly World w = new();
    public void Dispose() => Directory.Delete(profile, true);

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
            var descriptor = new HostEnrollment(reader, Account, Generation).Describe(profile, World.Keys("H").Dh.Public).Descriptor!;
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
    [InlineData("no-switch")]
    [InlineData("account")]
    [InlineData("generation")]
    [InlineData("not-pinned")]
    public void Refusal_precedes_any_custody_access_and_reveals_no_identity(string code)
    {
        var reader = code == "not-pinned" ? new HostRosterReader(Path.Combine(profile, "absent"), () => w.Now) : Reader(code != "no-switch");
        var enrollment = new HostEnrollment(reader,
            code == "account" ? "another-account" : Account, code == "generation" ? new string('f', 32) : Generation);
        var result = enrollment.Describe(profile, World.Keys("H").Dh.Public);
        Assert.Equal(code, result.Refusal);
        Assert.Null(result.Descriptor);
        Assert.False(Directory.Exists(Path.Combine(profile, "host-sign")));
        Assert.Equal("not-enrolled", enrollment.Status(null, w.Id("G")));
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
        var enrollment = new HostEnrollment(reader, Account, Generation);
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
