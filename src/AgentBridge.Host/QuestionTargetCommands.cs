using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public static class QuestionTargetCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<ISessionSource> createSource,
        TextWriter output, TextWriter error)
    {
        if (args.Count == 5 && args[3] == "--read-root") { args = args.Take(3).ToArray(); }
        if (args is not ["question-target", var id, var questionId]
            || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(questionId))
        {
            error.WriteLine("usage: agent-bridge-host question-target <session-id> <question-id> [--read-root <absolute-home>]");
            return 2;
        }

        int Report(string outcome, string? turn = null)
        {
            output.WriteLine(JsonSerializer.Serialize(new { id, question = questionId, outcome, turn }, ProtocolJson.Options));
            if (outcome != "pending") { error.WriteLine($"question-target: {outcome}"); }
            return outcome == "pending" ? 0 : 1;
        }

        try
        {
            await using var source = createSource();
            await source.RefreshAsync();
            var outcome = Current(source, id, questionId, out var session);
            string? answeredTurn = null;
            if (outcome == "no-longer-pending")
            {
                var history = await source.HistoryAsync(id, null, HistoryLimits.DefaultTurns);
                // History reads yield: revalidate current identity before using older evidence.
                await source.RefreshAsync();
                outcome = Current(source, id, questionId, out session);
                if (outcome == "no-longer-pending" && history.Missing is not true && history.Stale is not true
                    && history.Unavailable is null)
                {
                    var answered = history.Turns?.FirstOrDefault(t => t.Unreadable is not true && t.Parts.Any(p =>
                        p.Kind == HistoryPartKind.Question && p.Question?.Id == questionId
                        && p.Outcome is QuestionOutcome.Answered or QuestionOutcome.Approved or QuestionOutcome.Denied));
                    if (answered is not null) { outcome = "answered"; answeredTurn = answered.Id; }
                }
            }
            return Report(outcome, outcome == "pending" ? session?.Activity.Turn : answeredTurn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Report("unavailable");
        }
    }

    static string Current(ISessionSource source, string id, string questionId, out Session? session)
    {
        session = source.Get(id);
        if (source.Held is { } held && (session is null || held.Agents.Contains(session.Agent))) { return "unknown"; }
        if (session is null) { return "missing"; }
        if (session.Id != id) { return "stale"; }
        if (session.Question is null) { return "no-longer-pending"; }
        if (session.Question.Id != questionId) { return "stale"; }
        return session.Status == SessionStatus.Waiting ? "pending" : "unknown";
    }
}
