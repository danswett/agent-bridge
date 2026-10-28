using System.Text;

namespace AgentBridge.Protocol;

// Whether a session.reply can be typed at all. Shared so every host refuses the same replies
// for the same reasons, before anything is typed; the PWA mirrors it word for word.
public static class Replies
{
    // Null when the reply can be sent; otherwise why not, for a refused outcome. Whether the
    // session can take it now (a question pending, the agent, the platform) is the host's.
    public static string? Check(ReplyRequest reply)
    {
        var attachments = reply.Attachments ?? [];
        if (attachments.Count > AttachmentLimits.PerReply) { return $"a reply carries at most {AttachmentLimits.PerReply} attachments"; }
        if (attachments.Any(a => a is null || !Attachments.IsId(a.Id))) { return "each attachment must be the UUID of one sent with attachment.start"; }
        if (attachments.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachments.Count) { return "the same attachment is in the reply twice"; }
        var text = reply.Text ?? "";
        // Control characters are typed as spaces, so text of nothing else is empty too. With
        // attachments the words are optional, as at the machine.
        var blank = text.All(c => char.IsWhiteSpace(c) || char.IsControl(c));
        if (blank && attachments.Count == 0) { return "the reply is empty"; }
        if (Encoding.UTF8.GetByteCount(text) > Limits.ReplyText) { return $"the reply is longer than {Limits.ReplyText / 1024} KiB"; }
        // What the agent's input sees first once control characters have become spaces and
        // it has trimmed the line. A prompt beginning with / is a slash command and one
        // beginning with ! a shell command, in Copilot CLI and Claude Code alike: they would
        // run on the machine, not reach the agent, and could never be confirmed.
        var first = text.FirstOrDefault(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
        return first switch
        {
            '/' => "a reply cannot start with /, which the agent would run as a command; type commands at the machine",
            '!' => "a reply cannot start with !, which the agent would run as a shell command; type commands at the machine",
            _ => null,
        };
    }
}
