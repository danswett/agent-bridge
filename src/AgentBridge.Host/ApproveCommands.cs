using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public static class ApproveCommands
{
    public static async Task<int> RunAsync(string[] args, string root, Func<bool> refused,
        TextWriter output, TextWriter error)
    {
        int Fail(int code, string message) { error.WriteLine(message); return code; }
        var profile = "default";
        if (args is [.. var command, "--profile", var selected]) { args = command; profile = selected; }
        if (args is not ["approve", var id, var questionId, var request, var choice]
            || !Identity.IsValidProfile(profile) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(questionId)
            || !Guid.TryParseExact(request, "D", out var uuid) || uuid == Guid.Empty)
        {
            return Fail(2, "usage: approve <session> <question> <request-uuid> <approve|deny> [--profile <name>]");
        }
        if (choice is not ("approve" or "deny")) { return Fail(4, "refused: unsupported choice"); }
        if (refused()) { return Fail(4, "refused: person invocation required"); }
        var submitted = false;
        try
        {
            await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile)));
            var hello = await channel.HelloAsync();
            if (!hello.Capabilities.Contains(Capabilities.SessionsApprove)) { return Fail(4, "refused: approval unavailable"); }
            var session = await channel.ReadAsync<Session>(Ops.SessionGet, new { id });
            if (!Supported(session, id, questionId)) { return Fail(4, "refused: unsupported or stale approval"); }
            var shown = session.Question!;
            output.WriteLine(shown.Message);
            for (var i = 0; i < shown.Fields[0].Options!.Count; i++) { output.WriteLine($"{i + 1}. {shown.Fields[0].Options![i].Label}"); }
            output.WriteLine($"Choice: {choice}");
            await output.FlushAsync();
            var current = await channel.ReadAsync<Session>(Ops.SessionGet, new { id });
            if (!Supported(current, id, questionId)
                || JsonSerializer.Serialize(current.Question, ProtocolJson.Options) != JsonSerializer.Serialize(shown, ProtocolJson.Options))
            {
                return Fail(4, "stale: approval changed");
            }
            var answer = new AnswerRequest
            {
                Id = id, Question = questionId, Request = request,
                Fields = new Dictionary<string, JsonElement> { ["decision"] = JsonSerializer.SerializeToElement(choice) },
            };
            if (Answers.Check(shown, answer) is not null) { return Fail(4, "refused: unsupported answer"); }
            if (refused()) { return Fail(4, "refused: person invocation required"); }
            submitted = true;
            var data = await channel.CallAsync(Ops.SessionAnswer, answer, timeout: TimeSpan.FromSeconds(60));
            var result = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("outcome", out var state)
                && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
            switch (result)
            {
                case "delivered": output.WriteLine(result); return 0;
                case "refused" or "stale" or "busy": return Fail(4, result);
                default: return Fail(3, "unconfirmed");
            }
        }
        catch (HostErrorException) { return Fail(4, "refused: host declined approval"); }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return Fail(submitted ? 3 : 4, submitted ? "unconfirmed" : "refused: local approval unavailable");
        }
    }

    static bool Supported(Session session, string id, string questionId) =>
        session.Id == id && session.Agent == AgentKind.Copilot && session.Status == SessionStatus.Waiting
        && session.Question is { Kind: QuestionKind.Approval, Agent: null, Truncated: not true, AnswerAtMachine: not true, Escaped: null } question
        && question.Id == questionId && !string.IsNullOrWhiteSpace(question.Message) && !ShownText.Misleads(question.Message)
        && question.Fields is [{ Name: "decision", Type: FieldType.Choice, Required: true, AllowOther: not true,
            Title: null, Description: null, Default: null, Constraints: null,
            Options: [{ Value: "approve", Label: "Approve once" }, { Value: "deny", Label: "Deny" }] }];
}
