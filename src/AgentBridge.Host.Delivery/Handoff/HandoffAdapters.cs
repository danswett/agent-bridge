using System.Threading.Channels;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Handoff;

public sealed class FileHandoffTranscript(AgentKind kind, AgentHomes homes) : IHandoffTranscript
{
    static readonly HashSet<string> FileTools = new(StringComparer.OrdinalIgnoreCase)
        { "edit", "create", "write", "multiedit", "apply_patch" };

    public Task<HandoffDigest?> ReadAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.Run<HandoffDigest?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = SessionBundles.ReadHistory(kind, homes, sessionId, cancellationToken);
            if (history.Turns is not { Count: > 0 } turns) { return null; }
            var parts = turns.SelectMany(t => t.Parts).ToList();
            var response = parts.LastOrDefault(p => p.Kind == HistoryPartKind.Response && !string.IsNullOrWhiteSpace(p.Text))?.Text;
            var request = turns.LastOrDefault(t => t.Prompt is not null)?.Prompt?.Text;
            if (response is null && request is null) { return null; }
            var open = new List<string> { "Verify remaining work; recorded excerpts are not a completion claim." };
            if (request is not null) { open.Add("Last recorded request: " + request); }
            if (history.Before is not null || history.Partial == true || history.Start?.Limited == true)
            {
                open.Add("History is incomplete; read the original transcript for earlier or omitted details.");
            }
            var files = parts.Where(p => p.Kind == HistoryPartKind.Tool && p.Tool is { Status: ToolStatus.Done, Summary: not null } tool
                && FileTools.Contains(tool.Name.Split('.').Last())).Select(p => $"Recorded file-tool summary: {p.Tool!.Name}: {p.Tool.Summary}").Distinct().ToArray();
            return new HandoffDigest(response is null ? [] : ["Recorded response: " + response], open, files);
        }, cancellationToken);
}

public sealed class ReplyHandoffAgent(ISessionSource source, ISessionReplier replier) : IHandoffAgent
{
    public async Task<string?> AskForNoteAsync(string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        void Changed(object? sender, SessionChange change)
        {
            if (change.Id == sessionId) { changes.Writer.TryWrite(0); }
        }
        void UnbudgetedChanged(object? sender, string id)
        {
            if (id == sessionId) { changes.Writer.TryWrite(0); }
        }
        source.Changed += Changed;
        source.UnbudgetedChanged += UnbudgetedChanged;
        try
        {
            var before = source.GetForDelivery(sessionId);
            if (before is not { Status: SessionStatus.Idle, Question: null }
                || before.Agent is not (AgentKind.Copilot or AgentKind.Claude or AgentKind.Codex))
            {
                throw new InvalidOperationException("handoff requires an idle supported agent with no question waiting");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await replier.ReplyAsync(new ReplyRequest { Id = sessionId, Request = Guid.NewGuid().ToString("D"), Text = prompt },
                cancellationToken: cancellationToken).WaitAsync(cancellationToken);
            if (result.Outcome != AnswerOutcome.Delivered) { throw new InvalidOperationException(result.Reason ?? "the handoff prompt was not confirmed delivered"); }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = source.GetUnbudgeted(sessionId) ?? throw new InvalidOperationException("the session ended before answering its handoff request");
                if (session.Question is not null || session.Attention is not null)
                {
                    throw new InvalidOperationException("the session needs attention at the machine before it can finish its handoff");
                }
                var activity = session.Activity;
                if (session.Status == SessionStatus.Idle && activity.Turn is { } turn && turn != before.Activity.Turn
                    && activity.ResponseTurn == turn && CopilotReply.Matches(activity.UserMessage ?? "", prompt))
                {
                    // A delegate's top-level response is not the main agent's answer.
                    var own = activity.Source?.Response is null;
                    var note = own ? activity.Response : activity.Own?.Response;
                    if ((own ? activity.ResponseTruncated : activity.Own?.ResponseTruncated) == true)
                    {
                        throw new InvalidOperationException("the handoff response was truncated; no complete note can be saved");
                    }
                    if (!string.IsNullOrWhiteSpace(note)) { return note; }
                }
                await changes.Reader.ReadAsync(cancellationToken);
            }
        }
        finally { source.Changed -= Changed; source.UnbudgetedChanged -= UnbudgetedChanged; }
    }
}
