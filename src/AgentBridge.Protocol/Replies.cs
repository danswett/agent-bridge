using System.Text;
using System.Text.Json;

namespace AgentBridge.Protocol;

// Whether a session.reply can be typed at all. Shared so every host refuses the same replies
// for the same reasons, before anything is typed; the PWA mirrors it word for word.
public static class Replies
{
    // Null when the reply can be sent; otherwise why not, for a refused outcome. Whether the
    // session can take it now (a question pending, the agent, the platform) is the host's.
    public static string? Check(ReplyRequest reply)
    {
        if (reply.Attachments is { } a && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == 0))
        {
            return "attachments are not supported yet; send the text alone, or attach at the machine";
        }
        var text = reply.Text;
        // Control characters are typed as spaces, so text of nothing else is empty too.
        if (text is null || text.All(c => char.IsWhiteSpace(c) || char.IsControl(c))) { return "the reply is empty"; }
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
