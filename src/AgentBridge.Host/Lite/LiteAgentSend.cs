using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

public sealed record LiteSendReply(string Outcome, string? Code, string Request);

// The agent.send pipe op: an agent that passed agent.hello asks its host to send text to a
// session on another of the person's machines (AP5-lite L7, #1355). The sender is whoever the
// connection's credential says it is; nothing in the request names or changes that.
public static class LiteAgentSend
{
    public const string Op = "agent.send";
    public const int MaxText = 4096;

    public static async Task<object?> HandleAsync(AgentCredentials credentials, ILiteSender sender, IProtocolChannel channel, JsonElement data, CancellationToken ct = default)
    {
        if (credentials.Of(channel) is not { } caller)
        {
            throw new InvalidOperationException("refused: agent.send is for an agent connection that has passed agent.hello");
        }
        // Only the three fields are read, so a principal (or anything else) in the body is ignored.
        if (data.ValueKind != JsonValueKind.Object
            || Text(data, "machine") is not { Length: > 0 } machine
            || Text(data, "session") is not { Length: > 0 } session
            || Text(data, "text") is not { Length: > 0 } text)
        {
            throw new ArgumentException("agent.send needs machine, session and text");
        }
        if (text.Length > MaxText) { throw new ArgumentException($"refused: text is over {MaxText} characters"); }
        var outcome = await sender.SendAsync(caller, new LiteSendInput(machine, session, text), ct);
        return new LiteSendReply(outcome.Kind.ToString().ToLowerInvariant(), outcome.Code, outcome.Request);
    }

    static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

// Stands in until L3's sender is wired: with nothing to send through the mode is off, so the
// caller falls back to Home Assistant at once.
public sealed class LiteOffSender : ILiteSender
{
    public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct) =>
        Task.FromResult(new LiteOutcome(LiteKind.Off, null, ""));
}
