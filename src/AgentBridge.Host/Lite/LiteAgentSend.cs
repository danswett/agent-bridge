using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

public sealed record LiteSendReply(string Outcome, string? Code, string Request);

// The agent.send pipe op: an agent that passed agent.hello asks its host to send text to a
// session on another of the person's machines (AP5-lite L7, #1355). The sender is whoever the
// connection's credential says it is; nothing in the request names or changes that.
public static partial class LiteAgentSend
{
    public const string Op = "agent.send";
    public const int MaxText = 4096;

    public static async Task<object?> HandleAsync(AgentCredentials? credentials, ILiteSender? sender, IProtocolChannel channel, JsonElement data, CancellationToken ct = default)
    {
        if (credentials is null || !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("refused: agent.send isn't available on this host");
        }
        if (credentials.Of(channel) is not { } caller)
        {
            throw new InvalidOperationException("refused: agent.send is for an agent connection that has passed agent.hello");
        }
        // Only the three fields are read, so a principal (or anything else) in the body is ignored.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("refused: badRequest: agent.send needs machine, session and text"); }
        var machine = Field(data, "machine");
        var session = Field(data, "session");
        var text = Field(data, "text");
        if (machine is null || !MachinePattern().IsMatch(machine)) { throw new ArgumentException("refused: badMachine: machine is 1 to 64 letters, digits, '.', '_' or '-'"); }
        if (session is null || !SessionPattern().IsMatch(session)) { throw new ArgumentException("refused: badSession: session is agent_bridge_ and 16 lowercase hex digits"); }
        if (text is null || text.Length == 0) { throw new ArgumentException("refused: badText: text is required"); }
        if (text.Length > MaxText) { throw new ArgumentException($"refused: textTooLong: text is over {MaxText} characters"); }
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException("refused: badText: text must not be blank or hold control characters other than tab, CR and LF");
        }
        var outcome = await (sender ?? new LiteOffSender()).SendAsync(caller, new LiteSendInput(machine, session, text), ct);
        return new LiteSendReply(outcome.Kind.ToString().ToLowerInvariant(), outcome.Code, outcome.Request);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex MachinePattern();

    [GeneratedRegex(@"^agent_bridge_[0-9a-f]{16}\z")]
    private static partial Regex SessionPattern();

    static string? Field(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
// Stands in until L3's sender is wired: with nothing to send through the mode is off, so the
// caller falls back to Home Assistant at once.
public sealed class LiteOffSender : ILiteSender
{
    public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct) =>
        Task.FromResult(new LiteOutcome(LiteKind.Off, null, ""));
}
