using System.Text;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using NSec.Cryptography;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class Order73dLeakProbeTests
{
    sealed class Capture : IFeedbackUplink
    {
        public long MaxBytes => 8 * 1024 * 1024;
        public int Slots;
        public byte[]? Stored;
        public Task<string> RequestSlotAsync(long bytes, CancellationToken ct) { Slots++; return Task.FromResult("/synthetic-no-network"); }
        public Task UploadAsync(string url, byte[] bytes, CancellationToken ct) { Stored = bytes; return Task.CompletedTask; }
    }

    sealed class Counted(IFeedbackBundleSource source) : IFeedbackBundleSource
    {
        public int Reads;
        public Task<FeedbackGatherResult> GatherAsync(FeedbackRequest request, IProgress<FeedbackGatherStage>? progress, CancellationToken ct)
        {
            Reads++;
            return source.GatherAsync(request, progress, ct);
        }
    }

    [Theory]
    [InlineData("https://example.invalid/report?sig=SyntheticSignature73d")]
    [InlineData("BUILD_MODE=private_release73d")]
    [InlineData(@"Z:\private-project73d\file.txt")]
    [InlineData("ForeignMachine73d")]
    [InlineData("ForeignUser73d")]
    [InlineData("ForeignSession73d")]
    public async Task Ordinary_feedback_stays_disabled_before_collection_and_after_sender_refresh_send_retry_or_ready(string value)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "order73d-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "app.log"), value + "\n");
            var sources = new AppFeedbackSources(() => new FeedbackEnvironment("1.2.3", "stable", "Windows", "X64", 1, []),
                folder, Path.Combine(folder, "settings.json"), _ => Task.FromResult<AgentBridge.Local.AgentReadinessReport?>(null),
                (_, _, _) => Task.FromResult<AgentBridge.Protocol.HistoryResult?>(null));
            var source = new Counted(new FeedbackBundleSource(sources, privateNames: []));
            var request = new FeedbackRequest();
            using var key = Key.Create(KeyAgreementAlgorithm.X25519);
            var capture = new Capture();
            var sender = new FeedbackSender(capture, new FeedbackRecipient("order73d", key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
            var send = new FeedbackSendViewModel(sender, allowSending: request.Recovery is not null);
            Assert.Null(await send.PrepareAsync(source, request));
            Assert.Equal(FeedbackSendViewModel.OrdinaryUnavailableText, send.Message);
            send.RefreshSender(sender);
            send.RefreshRetry();
            await send.SendAsync();
            await send.RetryAsync();
            send.Ready(new FeedbackBundle(Encoding.UTF8.GetBytes("{}"), new byte[16], DateTimeOffset.UnixEpoch, []));
            await send.SendAsync();
            await send.RetryAsync();
            Assert.False(send.CanSend);
            Assert.False(send.CanRetry);
            Assert.Equal(0, source.Reads);
            Assert.Equal(0, capture.Slots);
            Assert.Null(capture.Stored);
        }
        finally { Directory.Delete(folder, true); }
    }
}
