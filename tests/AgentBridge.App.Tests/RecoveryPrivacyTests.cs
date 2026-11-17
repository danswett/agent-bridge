using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using AgentBridge.FeedbackEnvelope;
using NSec.Cryptography;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class RecoveryPrivacyTests : IDisposable
{
    readonly string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ab-recovery-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => Directory.Delete(folder, true);
    AppFeedbackSources Sources() => new(() => AppFeedbackSources.Describe("current", "prerelease", []),
        folder, Path.Combine(folder, "settings.json"), _ => Task.FromResult<AgentBridge.Local.AgentReadinessReport?>(null),
        (_, _, _) => Task.FromResult<AgentBridge.Protocol.HistoryResult?>(null));
    static string ForeignName() => "node-" + Guid.NewGuid().ToString("N")[..8];
    static (string[] Lines, string Needle) PrivateText(int kind)
    {
        var value = kind switch
        {
            0 => "word=MapleBoat7!",
            1 => "https://example.invalid/upload?sig=maple-seven",
            2 => new string(("ghp_" + new string('x', 36)).Select(c => (char)(c + 0xFEE0)).ToArray()),
            3 => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "private.txt"),
            4 => "BUILD_MODE=private-build",
            _ => ForeignName(),
        };
        return (kind == 0 ? ["pass", value] : [value], kind == 0 ? "MapleBoat7!" : value);
    }

    sealed class Uplink : IFeedbackUplink
    {
        public long MaxBytes => 8 * 1024 * 1024;
        public int Slots;
        public readonly List<byte[]> Uploaded = [];
        public Task<string> RequestSlotAsync(long bytes, CancellationToken ct) { Slots++; return Task.FromResult("/v1/feedback/test"); }
        public Task UploadAsync(string url, byte[] bytes, CancellationToken ct) { Uploaded.Add(bytes); return Task.CompletedTask; }
    }
    static IEnumerable<string> Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Text),
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Text(p.Value)),
        _ => [],
    };
    static void CheckShape(JsonElement root)
    {
        Assert.Equal(["bundleId", "createdAt", "recovery", "schema"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["ExceptionType", "HResult", "HangAt", "SecondsStalled", "Stage", "StartedAt", "Version"],
            root.GetProperty("recovery").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Decrypted_recovery_uploads_exclude_every_diagnostic_text_class(int kind)
    {
        var (lines, needle) = PrivateText(kind);
        File.WriteAllText(Path.Combine(folder, "app.log"), string.Join('\n', lines));
        File.WriteAllText(Path.Combine(folder, "crash.log"), string.Join('\n', lines));
        var marker = RunMarker.Start(folder, "1.2.3");
        marker.RecordHang(42, TimeSpan.FromSeconds(15), lines);
        var previous = RunMarker.Start(folder, "next").Previous;
        var machine = ForeignName();
        var request = await Sources().RecoveryRequestAsync(previous, machine, Guid.NewGuid().ToString(), default);
        var bundle = Assert.IsType<FeedbackGatherResult.Ready>(await new FeedbackBundleSource(Sources(), privateNames: []).GatherAsync(request, null, default)).Bundle;
        using var key = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var recipient = new FeedbackRecipient("test", key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var uplink = new Uplink();
        Assert.IsType<FeedbackSendResult.Sent>(await new FeedbackSender(uplink, recipient).SendAsync(bundle, default));
        var opened = FeedbackEnvelopeV1.Open(Assert.Single(uplink.Uploaded), key.Export(KeyBlobFormat.RawPrivateKey));
        Assert.Equal(bundle.CanonicalBytes.ToArray(), opened);
        using var doc = JsonDocument.Parse(opened);
        Assert.DoesNotContain(Text(doc.RootElement), s => s.Contains(needle, StringComparison.Ordinal));
        Assert.DoesNotContain(Text(doc.RootElement), s => s.Contains(machine, StringComparison.Ordinal));
        CheckShape(doc.RootElement);
        var recovery = doc.RootElement.GetProperty("recovery");
        Assert.Equal("1.2.3", recovery.GetProperty("Version").GetString());
        Assert.Equal("hang", recovery.GetProperty("Stage").GetString());
        Assert.Equal(15, recovery.GetProperty("SecondsStalled").GetDouble());
    }

    sealed class NoReads : IFeedbackSources
    {
        public int Reads;
        Exception Read() { Reads++; return new InvalidOperationException(); }
        public FeedbackEnvironment Environment() => throw Read();
        public Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken ct) => throw Read();
        public Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken ct) => throw Read();
        public Task<IReadOnlyList<string>> JournalTailAsync(int n, CancellationToken ct) => throw Read();
        public Task<IReadOnlyList<string>> CrashRecordsAsync(int n, CancellationToken ct) => throw Read();
        public Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string id, int n, CancellationToken ct) => throw Read();
    }

    [Fact]
    public async Task Recovery_never_reads_unrelated_sources_or_attaches_notes_images_turns_or_unvalidated_metadata()
    {
        var request = new FeedbackRequest { Recovery = new(new PreviousRun(true, "BUILD_MODE=private", Crash: ForeignName()), ForeignName(), "private-session", []),
            Note = "private note", ScreenshotPng = [1, 2, 3], SessionTurnsFor = "private-session" };
        var sources = new NoReads();
        var bundle = Assert.IsType<FeedbackGatherResult.Ready>(await new FeedbackBundleSource(sources).GatherAsync(request, null, default)).Bundle;
        Assert.Equal(0, sources.Reads);
        using var doc = JsonDocument.Parse(bundle.Text);
        CheckShape(doc.RootElement);
        var recovery = doc.RootElement.GetProperty("recovery");
        Assert.Equal("crash", recovery.GetProperty("Stage").GetString());
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("Version").ValueKind);
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("ExceptionType").ValueKind);
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("HResult").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Sender_refuses_non_allowlisted_recovery_fields_before_requesting_a_slot(int kind)
    {
        var request = await Sources().RecoveryRequestAsync(new PreviousRun(true, "1.2.3"), ForeignName(), null, default);
        var bundle = Assert.IsType<FeedbackGatherResult.Ready>(await new FeedbackBundleSource(Sources()).GatherAsync(request, null, default)).Bundle;
        var root = JsonNode.Parse(bundle.Text)!;
        var (lines, needle) = PrivateText(kind);
        if (kind == 0) { root["recovery"]!["LastLogLines"] = JsonSerializer.SerializeToNode(lines); }
        else if (kind == 1) { root["recovery"]!["Version"] = needle; }
        else if (kind == 2) { root["recovery"]!["ExceptionType"] = needle; }
        else if (kind == 3) { root["recovery"]!["Stage"] = needle; }
        else if (kind == 4) { root["note"] = needle; }
        else { root["recovery"]!["Machine"] = needle; }
        using var key = Key.Create(KeyAgreementAlgorithm.X25519);
        var uplink = new Uplink();
        var sender = new FeedbackSender(uplink, new FeedbackRecipient("test", key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
        var result = await sender.SendAsync(new FeedbackBundle(Encoding.UTF8.GetBytes(root.ToJsonString()), bundle.BundleId, bundle.CreatedAt, []), default);
        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never), result);
        Assert.Equal(0, uplink.Slots);
        Assert.Empty(uplink.Uploaded);
    }
}
