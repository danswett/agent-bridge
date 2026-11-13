using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;
using AgentBridge.Transport;
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
            "genesis" => JsonValue.Create(Codec.Base64Url(new byte[32])), "v" => JsonValue.Create(2),
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
            "duplicate" => text.Replace("\"v\":1", "\"v\":1,\"v\":1"),
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

    public void Dispose()
    {
        File.Delete(TransitionPath);
        File.Delete(TransitionPath + ".lock");
        File.Delete(PathName);
        Directory.Delete(folder);
    }
}
