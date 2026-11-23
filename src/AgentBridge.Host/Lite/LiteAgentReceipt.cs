using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

// The agent.receipt pipe op (docs/protocol/agent-lite.md "Receipt lookup", #3875): what the first attempt of this credential's own unconfirmed
// agent.send became on the target. agent.send's caller rules (the store refuses script credentials) and audit; sends no text.
public static class LiteAgentReceipt
{
    public const string Op = "agent.receipt";

    public static async Task<object?> HandleAsync(AgentCredentials? credentials, ILiteReceipts? receipts, IProtocolChannel channel, JsonElement data, CancellationToken ct = default, bool? windows = null, Func<AgentAuditEntry, string?, bool>? appendAttempt = null)
    {
        credentials?.CheckScriptOperation(channel, Op);
        if (credentials is null || receipts is null || !(windows ?? OperatingSystem.IsWindows()))
        {
            throw new InvalidOperationException("refused: agent.receipt isn't available on this host");
        }
        if (credentials.Of(channel) is not { } caller)
        {
            throw new InvalidOperationException("refused: agent.receipt is for an agent connection that has passed agent.hello");
        }
        // Only `request` is read, so a principal (or anything else) in the body is ignored.
        var request = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("request", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (request is null || !Guid.TryParseExact(request, "D", out var parsed) || parsed.ToString("D") != request)
        {
            throw new ArgumentException("refused: badRequest: agent.receipt needs request, the id agent.send returned");
        }
        var attempt = new AgentAuditEntry("out", Guid.NewGuid().ToString("D"), credentials.PrincipalOf(caller), caller.Chain, Op, request, "attempted");
        bool written;
        try { written = appendAttempt?.Invoke(attempt, null) == true; }
        catch (Exception) { throw AuditUnavailable(); }
        if (!written) { throw AuditUnavailable(); }
        LiteReceiptReply? reply;
        try { reply = await receipts.ReceiptAsync(caller, request, ct); }
        catch (Exception)
        {
            AppendTerminal("unknown");
            throw;
        }
        AppendTerminal(reply?.Outcome ?? "refused");
        return reply ?? throw new ArgumentException("refused: unknownRequest: this credential has no unconfirmed send with that request id");

        void AppendTerminal(string terminal)
        {
            bool terminalWritten;
            try { terminalWritten = appendAttempt?.Invoke(attempt with { Outcome = terminal }, null) == true; }
            catch (Exception) { throw new InvalidOperationException("refused: auditUnwritable: the terminal agent audit outcome could not be written"); }
            if (!terminalWritten) { throw new InvalidOperationException("refused: auditUnwritable: the terminal agent audit outcome could not be written"); }
        }
    }

    static InvalidOperationException AuditUnavailable() =>
        new("refused: auditUnwritable: the agent audit log could not be written; nothing was asked");
}
