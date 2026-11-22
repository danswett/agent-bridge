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

    public static async Task<object?> HandleAsync(AgentCredentials? credentials, ILiteSender? sender, IProtocolChannel channel, JsonElement data, CancellationToken ct = default, bool? windows = null, Func<AgentAuditEntry, string?, bool>? appendAttempt = null)
    {
        credentials?.CheckScriptOperation(channel, Op);
        if (credentials is null || !(windows ?? OperatingSystem.IsWindows()))
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
        if (machine is null || !Wire.IsDeviceId(machine)) { throw new ArgumentException(AgentLiteSender.MachineRefusal(machine, AgentLiteSender.BadMachine, "must be an exact device ID; names never route")); }
        if (session is null || !SessionPattern().IsMatch(session)) { throw new ArgumentException("refused: badSession: session is agent_bridge_ and 16 lowercase hex digits"); }
        if (text is null || text.Length == 0) { throw new ArgumentException("refused: badText: text is required"); }
        if (text.Length > MaxText) { throw new ArgumentException($"refused: textTooLong: text is over {MaxText} characters"); }
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException("refused: badText: text must not be blank or hold control characters other than tab, CR and LF");
        }
        // This local attempt id is not the relay request id, which the sender creates later.
        var attempt = new AgentAuditEntry("out", Guid.NewGuid().ToString("D"), credentials.PrincipalOf(caller),
            caller.Chain, Op, $"{machine}/{session}", "attempted");
        bool written;
        try { written = appendAttempt?.Invoke(attempt, text) == true; }
        catch (Exception) { throw AuditUnavailable(); }
        if (!written) { throw AuditUnavailable(); }
        LiteOutcome outcome;
        try { outcome = await (sender ?? new LiteOffSender()).SendAsync(caller, new LiteSendInput(machine, session, text), ct); }
        catch (Exception)
        {
            AppendTerminal("unconfirmed");
            throw;
        }
        var name = outcome.Kind.ToString().ToLowerInvariant();
        AppendTerminal(name);
        return new LiteSendReply(name, outcome.Code, outcome.Request);

        void AppendTerminal(string terminal)
        {
            bool terminalWritten;
            try { terminalWritten = appendAttempt?.Invoke(attempt with { Outcome = terminal }, text) == true; }
            catch (Exception) { throw TerminalAuditUnavailable(); }
            if (!terminalWritten) { throw TerminalAuditUnavailable(); }
        }
    }

    static InvalidOperationException AuditUnavailable() =>
        new("refused: auditUnwritable: the agent audit log could not be written; nothing was sent");

    static InvalidOperationException TerminalAuditUnavailable() =>
        new("refused: auditUnwritable: the terminal agent audit outcome could not be written; the message may have been sent");

    [GeneratedRegex(@"^agent_bridge_[0-9a-f]{16}\z")]
    private static partial Regex SessionPattern();

    static string? Field(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
// An unwired sender reports off; the host never falls back to another transport or target.
public sealed class LiteOffSender : ILiteSender
{
    public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct) =>
        Task.FromResult(new LiteOutcome(LiteKind.Off, null, ""));
}
