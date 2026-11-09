using System.Globalization;
using System.Text;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Parsing;

// A Codex approval's question id: "<DecisionId>#<digest>" (#958).
//
// The DecisionId is the generation the hook wrote into its marker, and it does not bind the
// command: Codex can list the same generation again with another command. Keyed by the
// DecisionId alone, an Approve given for one command was typed into the next, plain, cut or
// escaped, which the person never saw. So the id also carries a digest of what the person is
// shown, the same way a Copilot permission prompt's does (CopilotPermissionPrompt): the tool,
// the whole command, whether it was cut, the hook's preview line, the message built from them,
// and whether that content alone holds it at the machine or truncated. An answer names both
// the generation and exactly what it approves, and a host that finds the marker now reads
// differently types nothing.
//
// Whether agent-ha-bridge has already typed into the approval (the marker's DeliveryAttempted)
// is left out: it is checked on its own before typing, and an id that changed with it would
// turn that refusal into a stale one.
public static class CodexApprovalId
{
    // The id of the approval this marker reads as now, rebuilt as the session's listing builds it.
    // Null for a marker that is not there or not readable.
    public static string? Of(ApprovalMarkerRead marker) =>
        marker is { State: ApprovalMarkerState.Present, DecisionId: { } decision }
            ? QuestionMapper.CodexApproval(decision, marker.Tool, marker.Command, marker.CommandCut, marker.Question ?? "", DateTimeOffset.UnixEpoch, atMachine: false, lossy: marker.Lossy).Id
            : null;

    // Strict: a non-blank DecisionId, one '#', then 16 lowercase hex digits. Anything else (the
    // bare DecisionId an older host listed, a rollout approval's call id) is not one.
    public static (string DecisionId, string Digest)? Parse(string? id)
    {
        if (id is null) { return null; }
        var at = id.LastIndexOf('#');
        if (at <= 0) { return null; }
        var (decision, digest) = (id[..at], id[(at + 1)..]);
        if (string.IsNullOrWhiteSpace(decision) || digest.Length != 16 || !digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) { return null; }
        return (decision, digest);
    }

    internal static string For(string decisionId, string? tool, string? command, bool cut, string preview, Question shown, string? lossy = null) =>
        $"{decisionId}#{Digest(tool, command, cut, preview, shown, lossy)}";

    // Each part with its length, so no part can spell its neighbour; an absent one (a marker
    // without a tool or command) is written apart from an empty one.
    // lossy: the marker as written, for one the tolerant read had to rewrite, so commands that read
    // alike once rewritten (\uD800, \uD801, a literal U+FFFD) are still different approvals.
    static string Digest(string? tool, string? command, bool cut, string preview, Question shown, string? lossy)
    {
        var b = new StringBuilder("\0codex-approval/1");
        void Part(string? text)
        {
            b.Append('\0');
            if (text is null) { b.Append('~'); return; }
            b.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
        }
        Part(tool);
        Part(command);
        Part(cut ? "cut" : "");
        Part(preview);
        Part(shown.Message);
        Part(shown.Truncated == true ? "truncated" : "");
        Part(shown.AnswerAtMachine == true ? "at-machine" : "");
        if (lossy is not null) { b.Append("\0lossy"); Part(lossy); }
        return CopilotPermission.IdentityHash(b.ToString());
    }
}
