using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Roster;
using Xunit;

public sealed partial class HostRosterPinTests
{
    Instant supportUtc = Instant.Parse("2026-11-04T12:00:00.123456789Z");
    HostRosterReader SupportReader() => PinnedReader(() => supportUtc);
    static string SupportTime(Instant at) => DateTimeOffset.FromUnixTimeSeconds(at.Seconds).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + $".{at.Nanos:D9}Z";
    static TestKey SupportKey(string id) => TestKey.Named("2381 throwaway capability " + id);
    sealed record SupportView(World World, HostRosterReader Reader) : IVerifierSupportRoster
    {
        public string Account => RosterFixture.Account;
        public string Generation => RosterFixture.Generation;
        public byte[] Genesis => World.Entry("G").Hash;
        public IReadOnlyList<byte[]> Heads => Reader.Verified!.Heads;
        public IReadOnlyList<VerifierSupportMember> Members => new[] { "G", "B", "H" }.Select(label =>
            new VerifierSupportMember(World.Id(label), World.Entry(label).Hash, label == "H" ? "host" : "phone", SupportKey(World.Id(label)).Public))
            .OrderBy(member => member.Id, StringComparer.Ordinal).ToArray();
        public bool Covers(IReadOnlyList<byte[]> heads) => heads.Count == Heads.Count && heads.Zip(Heads).All(pair => pair.First.SequenceEqual(pair.Second));
    }
    PersonCommand SupportCommand() => Command(request: Guid.NewGuid().ToString("D"), issued: SupportTime(supportUtc), expires: SupportTime(supportUtc.AddSeconds(60)));
    VerifierSupportContext StoredSupportContext() => new(Context.Host, SupportCommand(), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
        supportUtc, supportUtc.AddSeconds(60), supportUtc);
    byte[] SupportWire(HostRosterReader reader, VerifierSupportContext collection) =>
        JsonSerializer.SerializeToUtf8Bytes(new SupportView(w, reader).Members.Select(member =>
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, account = RosterFixture.Account, generation = RosterFixture.Generation, genesis = Codec.Base64Url(w.Entry("G").Hash),
                by = member.Id, add = Codec.Base64Url(member.Add), purpose = member.Kind == "host" ? "hostSign" : "capSign",
                key = Codec.Base64Url(Framing.Sha256(member.Key!)), versions = new[] { 2 },
                frontier = Codec.Base64Url(VerifierSupport.Frontier(reader.Verified!.Heads)), verifiers = Codec.Base64Url(VerifierSupport.Set(new SupportView(w, reader).Members)),
                target = collection.Host, request = collection.Command.Body.Request, consent = Codec.Base64Url(Framing.Sha256(PersonCommand.Signed(collection.Command.BodyBytes))),
                nonce = Codec.Base64Url(collection.Nonce), issuedAt = SupportTime(collection.IssuedAt), expiresAt = SupportTime(collection.ExpiresAt),
            });
            return new { body = Codec.Base64Url(body), sig = Codec.Base64Url(SupportKey(member.Id).Sign(VerifierSupport.Message(body))) };
        }));
    string? RecordSupport(HostRosterReader reader, VerifierSupportContext collection, byte[]? wire = null) =>
        reader.RecordVerifierSupport(wire ?? SupportWire(reader, collection), new SupportView(w, reader), collection);

    [Fact]
    public void V1_snapshot_load_writes_nothing_and_first_evidence_commit_writes_v2_without_losing_command_state()
    {
        var reader = SupportReader();
        Assert.Null(reader.RecordCommand(Command(), Context));
        var legacy = Snapshot();
        legacy["v"] = 1; legacy.Remove("evidence"); legacy.Remove("capabilityRequests");
        ReplaceSnapshot(legacy);
        var v1 = File.ReadAllBytes(TransitionPath);
        reader = SupportReader();
        Assert.Equal("unsupported-verifier", reader.CheckAcceptHostSign(Command(), Context));
        Assert.Equal(v1, File.ReadAllBytes(TransitionPath));
        var collection = StoredSupportContext();
        var wire = SupportWire(reader, collection);
        supportUtc = supportUtc.AddSeconds(1);
        Assert.Null(RecordSupport(reader, collection, wire));
        Assert.Equal(2, Snapshot()["v"]!.GetValue<int>());
        Assert.Equal(SupportTime(supportUtc), Snapshot()["highWater"]!.GetValue<string>());
        Assert.Equal(wire, Codec.FromBase64Url(Snapshot()["evidence"]!["collection"]!.GetValue<string>(), "collection"));
        Assert.Single(Snapshot()["requests"]!.AsArray());
        Assert.False(reader.Verified!.AcceptHostSign);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("generation")]
    public void Capability_state_cannot_be_carried_to_a_different_pinned_identity(string field)
    {
        var reader = SupportReader();
        Assert.Null(RecordSupport(reader, StoredSupportContext()));
        var snapshot = Snapshot();
        snapshot[field] = field == "account" ? "other-account" : new string('f', 32);
        ReplaceSnapshot(snapshot);
        var before = File.ReadAllBytes(TransitionPath);
        var pin = File.ReadAllBytes(PathName);
        reader = SupportReader();
        Assert.Equal("pin-mismatch", RecordSupport(reader, StoredSupportContext()));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        Assert.Equal(pin, File.ReadAllBytes(PathName));
    }

    // Part A bounds committed records only; #2410 adds outstanding reservations at admission.
    [Fact]
    public void Thirty_two_live_capability_records_refuse_a_thirty_third_without_eviction()
    {
        var reader = SupportReader();
        for (var i = 0; i < 32; i++) { Assert.Null(RecordSupport(reader, StoredSupportContext())); }
        var before = File.ReadAllBytes(TransitionPath);
        Assert.Equal("transition-store", RecordSupport(reader, StoredSupportContext()));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        Assert.Equal(32, Snapshot()["capabilityRequests"]!.AsArray().Count);
    }

    [Fact]
    public void Failed_atomic_replace_leaves_no_consumed_record_visible_and_the_write_can_retry()
    {
        var reader = SupportReader();
        Assert.Null(RecordSupport(reader, StoredSupportContext()));
        var collection = StoredSupportContext();
        var before = File.ReadAllBytes(TransitionPath);
        reader.BeforeTransitionCommit = () => { Assert.Equal(before, File.ReadAllBytes(TransitionPath)); throw new IOException("private-marker"); };
        Assert.Equal("transition-store", RecordSupport(reader, collection));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
        Assert.Empty(Directory.GetFiles(folder, "roster-transition.json.*.tmp"));
        reader.BeforeTransitionCommit = null;
        Assert.Null(RecordSupport(reader, collection));
        Assert.Equal(2, Snapshot()["capabilityRequests"]!.AsArray().Count);
        Assert.False(reader.Verified!.AcceptHostSign);
    }

    [Theory]
    [InlineData(0, "unsupported-verifier")]
    [InlineData(1, "pin-unreadable")]
    public void The_private_snapshot_accepts_exactly_256_KiB_but_refuses_one_byte_more_without_repair(int extra, string expected)
    {
        var reader = SupportReader();
        Assert.Null(RecordSupport(reader, StoredSupportContext()));
        var snapshot = Snapshot().ToJsonString();
        File.Delete(TransitionPath);
        Write(snapshot + new string(' ', 262144 + extra - Encoding.UTF8.GetByteCount(snapshot)), TransitionPath);
        var before = File.ReadAllBytes(TransitionPath);
        Assert.Equal(expected, SupportReader().CheckAcceptHostSign(Command(), Context));
        Assert.Equal(before, File.ReadAllBytes(TransitionPath));
    }
}
