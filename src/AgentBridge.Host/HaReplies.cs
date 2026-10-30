using System.Security.Cryptography;
using System.Text;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// A reply from a Home Assistant card, through the host's own reply delivery: its files go into
// the attachment store as the desktop app's do over the local pipe (direct, in chunks), and the
// reply names them, so they are staged and typed exactly as an app's attachments are. Null where
// this host cannot type into sessions, so the cards say so rather than offer a dead reply.
public static class HaReplies
{
    // Whose attachments these are in the store: the cards', never a device's.
    public const string Peer = "home-assistant";

    public static Func<HaReply, CancellationToken, Task<ReplyResult>>? For(ISessionReplier? replies, AttachmentStore? attachments)
    {
        if (replies is null or UnsupportedPlatformReplier) { return null; }
        return async (reply, ct) =>
        {
            if (reply.Files.Count == 0)
            {
                return await replies.ReplyAsync(new ReplyRequest { Id = reply.SessionId, Request = reply.Request, Text = reply.Text }, Peer, ct);
            }
            if (attachments is null || !replies.TakesAttachments) { return Refused("this host does not take attachments"); }
            if (reply.Files.Count > AttachmentLimits.PerReply) { return Refused($"a reply carries at most {AttachmentLimits.PerReply} attachments"); }
            var refs = new List<AttachmentRef>();
            try
            {
                try
                {
                    foreach (var file in reply.Files) { refs.Add(Put(attachments, file)); }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    return Refused($"an attachment could not be taken: {e.Message}");
                }
                return await replies.ReplyAsync(new ReplyRequest
                {
                    Id = reply.SessionId,
                    Request = reply.Request,
                    Text = reply.Text,
                    Attachments = refs,
                }, Peer, ct);
            }
            finally
            {
                // Whatever the reply did not take goes now, not when the store's hold lapses.
                foreach (var r in refs) { attachments.Discard(Peer, r.Id); }
            }
        };
    }

    static AttachmentRef Put(AttachmentStore attachments, HaReplyFile file)
    {
        var id = Guid.NewGuid().ToString("D");
        var start = new AttachmentStart
        {
            Attachment = id,
            Name = Name(file.Name),
            Type = file.Type is { } t && !string.IsNullOrWhiteSpace(t) && Encoding.UTF8.GetByteCount(t) <= AttachmentLimits.Type ? t : null,
            Size = file.Bytes.Length,
            Sha256 = Attachments.Base64Url(SHA256.HashData(file.Bytes)),
        };
        attachments.Start(Peer, start, direct: true);
        try
        {
            for (var offset = 0; offset < file.Bytes.Length; offset += AttachmentLimits.Chunk)
            {
                attachments.Chunk(Peer, new AttachmentChunk
                {
                    Attachment = id,
                    Offset = offset,
                    Data = Convert.ToBase64String(file.Bytes, offset, Math.Min(AttachmentLimits.Chunk, file.Bytes.Length - offset)),
                });
            }
        }
        catch
        {
            attachments.Discard(Peer, id);
            throw;
        }
        return new AttachmentRef { Id = id };
    }

    // The card's name for the file, cut to fit the store's limit with its extension kept: the
    // store names the staged file from it, and the agent reads the kind from the extension.
    internal static string Name(string given)
    {
        var name = string.IsNullOrWhiteSpace(given) ? "attachment" : given.Trim();
        if (Encoding.UTF8.GetByteCount(name) <= AttachmentLimits.Name) { return name; }
        var extension = Path.GetExtension(name);
        if (extension.Length > 16) { extension = ""; }
        var stem = name[..^extension.Length];
        while (stem.Length > 0 && Encoding.UTF8.GetByteCount(stem) + Encoding.UTF8.GetByteCount(extension) > AttachmentLimits.Name)
        {
            stem = stem[..^1];
            // Never half a surrogate pair.
            if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) { stem = stem[..^1]; }
        }
        return stem + extension;
    }

    static ReplyResult Refused(string why) => new(AnswerOutcome.Refused, why);
}
