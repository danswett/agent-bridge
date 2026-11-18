using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class ReplyCommands
{
    public static async Task<int> RunAsync(string[] args, string root, Func<bool> refused,
        TextWriter output, TextWriter error)
    {
        int Fail(int code, string message) { error.WriteLine(message); return code; }
        var profile = "default";
        if (args is [.. var command, "--profile", var selected]) { args = command; profile = selected; }
        if (args is not ["reply", var id, var request, var text]
            || !Identity.IsValidProfile(profile) || string.IsNullOrWhiteSpace(id)
            || !Guid.TryParseExact(request, "D", out var uuid) || uuid == Guid.Empty)
        {
            return Fail(2, "usage: reply <session> <request-uuid> <text> [--profile <name>]");
        }
        var reply = new ReplyRequest { Id = id, Request = request, Text = text };
        if (Replies.Check(reply) is { } problem) { return Fail(2, problem); }
        if (refused()) { return Fail(4, "refused: person invocation required"); }
        var submitted = false;
        try
        {
            await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile)));
            var hello = await channel.HelloAsync();
            if (!hello.Capabilities.Contains(Capabilities.SessionsReply)) { return Fail(4, "refused: replies unavailable"); }
            var session = Received.Read<Session>(await channel.CallAsync(Ops.SessionGet, new { id }), out var exact);
            if (!exact || session.Id != id || session.Question is not null || session.Status is not (SessionStatus.Idle or SessionStatus.Working))
            {
                return Fail(4, "refused: session unavailable or waiting for an answer");
            }
            if (refused()) { return Fail(4, "refused: person invocation required"); }
            submitted = true;
            var response = await channel.RequestAsync(Ops.SessionReply, reply, timeout: TimeSpan.FromSeconds(90));
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("ok", out var ok)) { return Fail(3, "unconfirmed"); }
            if (ok.ValueKind == JsonValueKind.False && !response.TryGetProperty("result", out _)
                && response.TryGetProperty("error", out var hostError) && hostError.ValueKind == JsonValueKind.String)
            {
                _ = Received.Read<string>(hostError);
                return Fail(4, "refused: host declined reply");
            }
            if (ok.ValueKind != JsonValueKind.True || response.TryGetProperty("error", out _)
                || !response.TryGetProperty("result", out var data)) { return Fail(3, "unconfirmed"); }
            var outcome = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("outcome", out var state)
                && state.ValueKind == JsonValueKind.String ? Received.Read<string>(state) : null;
            switch (outcome)
            {
                case "delivered": output.WriteLine(outcome); return 0;
                case "refused" or "stale" or "busy": return Fail(4, outcome);
                default: return Fail(3, "unconfirmed");
            }
        }
        catch (HostErrorException) { return Fail(4, "refused: host declined reply"); }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return Fail(submitted ? 3 : 4, submitted ? "unconfirmed" : "refused: local reply unavailable");
        }
    }
}
