using AgentBridge.HomeAssistant;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using Xunit;

// A reply from a Home Assistant card through the host's own reply delivery: its files arrive in
// the attachment store as an app's do, the reply names them, and nothing is left held afterwards.
public sealed class HaRepliesTests : IDisposable
{
    readonly string staging = Path.Combine(Path.GetTempPath(), "abhr-" + Guid.NewGuid().ToString("N"));
    readonly AttachmentStore store;

    public HaRepliesTests() => store = new AttachmentStore(new AttachmentOptions { StagingRoot = staging }, sweep: false);

    public void Dispose()
    {
        store.Dispose();
        if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
    }

    // A reply delivery that stages what it is given, as ReplyService does, and keeps the bytes.
    sealed class Typist(AttachmentStore store, AnswerOutcome outcome = AnswerOutcome.Delivered, bool attachments = true) : ISessionReplier
    {
        public List<(ReplyRequest Request, string? Peer, List<byte[]> Files)> Replies { get; } = [];
        public bool Available => true;
        public bool TakesAttachments => attachments;

        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default)
        {
            var files = new List<byte[]>();
            if (request.Attachments is { Count: > 0 } refs)
            {
                var (claim, problem) = store.Claim(peer!, refs);
                if (claim is null) { return Task.FromResult(new ReplyResult(AnswerOutcome.Refused, problem)); }
                files.AddRange(store.Stage(claim).Select(File.ReadAllBytes));
                if (outcome == AnswerOutcome.Delivered) { store.Consume(claim); } else { store.Release(claim); }
            }
            Replies.Add((request, peer, files));
            return Task.FromResult(new ReplyResult(outcome, outcome == AnswerOutcome.Delivered ? null : "the console refused the keys"));
        }
    }

    static byte[] Png(int size)
    {
        var b = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        Random.Shared.NextBytes(b.AsSpan(8));
        return b;
    }

    static HaReply Reply(params HaReplyFile[] files) => new("6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20", Guid.NewGuid().ToString(), "What is this?", files);

    [Fact]
    public async Task Words_alone_go_to_the_reply_delivery_as_the_cards()
    {
        var typist = new Typist(store);
        var reply = Reply();
        var result = await HaReplies.For(typist, store)!(reply, default);

        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        var (request, peer, _) = Assert.Single(typist.Replies);
        Assert.Equal(reply.SessionId, request.Id);
        Assert.Equal(reply.Request, request.Request);
        Assert.Equal("What is this?", request.Text);
        Assert.Null(request.Attachments);
        Assert.Equal(HaReplies.Peer, peer);
    }

    [Fact]
    public async Task Files_arrive_in_the_store_in_chunks_the_reply_names_them_and_nothing_is_left_held()
    {
        var typist = new Typist(store);
        // One over the in-channel limit, which only a direct channel takes in chunks.
        var shot = Png(300 * 1024);
        var notes = "line one\nline two\n"u8.ToArray();

        var result = await HaReplies.For(typist, store)!(Reply(new HaReplyFile("shot.png", "image/png", shot), new HaReplyFile("notes.txt", "text/plain", notes)), default);

        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        var (request, _, files) = Assert.Single(typist.Replies);
        Assert.Equal(2, request.Attachments!.Count);
        Assert.Equal(shot, files[0]);
        Assert.Equal(notes, files[1]);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Files_of_a_reply_that_reached_nothing_are_not_left_held()
    {
        var typist = new Typist(store, AnswerOutcome.Refused);

        var result = await HaReplies.For(typist, store)!(Reply(new HaReplyFile("a.png", null, Png(100))), default);

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Single(typist.Replies);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task More_files_than_a_reply_carries_or_a_host_without_attachments_is_refused_before_anything_is_typed()
    {
        var typist = new Typist(store);
        var many = Enumerable.Range(0, AttachmentLimits.PerReply + 1).Select(i => new HaReplyFile($"{i}.png", null, Png(64))).ToArray();
        Assert.Equal(AnswerOutcome.Refused, (await HaReplies.For(typist, store)!(Reply(many), default)).Outcome);

        var without = new Typist(store, attachments: false);
        Assert.Equal(AnswerOutcome.Refused, (await HaReplies.For(without, store)!(Reply(new HaReplyFile("a.png", null, Png(64))), default)).Outcome);
        Assert.Equal(AnswerOutcome.Refused, (await HaReplies.For(typist, null)!(Reply(new HaReplyFile("a.png", null, Png(64))), default)).Outcome);

        Assert.Empty(typist.Replies);
        Assert.Empty(without.Replies);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_host_that_cannot_type_into_sessions_takes_no_replies_from_the_cards()
    {
        Assert.Null(HaReplies.For(new UnsupportedPlatformReplier(), store));
        Assert.Null(HaReplies.For(null, store));
    }

    [Fact]
    public void A_name_too_long_for_the_store_is_cut_with_its_extension_kept()
    {
        var name = HaReplies.Name(new string('é', 300) + ".png");
        Assert.EndsWith(".png", name, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= AttachmentLimits.Name);
        Assert.Equal("attachment", HaReplies.Name("  "));
        Assert.Equal("notes.txt", HaReplies.Name("notes.txt"));
    }
}
