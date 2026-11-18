using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal sealed class NotifyOwnerService(AgentCredentials? credentials, AgentAuditLog? audit, TimeProvider time, SessionService? sessions)
{
    readonly object gate = new();
    readonly Dictionary<string, (DateTimeOffset At, int Count)> windows = new(StringComparer.Ordinal);
    static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<NotifyOwnerResult> HandleAsync(IProtocolChannel channel, JsonElement data)
    {
        var caller = credentials?.Of(channel);
        var requestId = Guid.NewGuid();
        try
        {
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("request", out var id)
                && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var parsed) && parsed != Guid.Empty) { requestId = parsed; }
        }
        catch (InvalidOperationException) { return Refuse("badRequest"); }
        NotifyOwnerResult Refuse(string reason)
        {
            if (audit?.Append(new("local", requestId.ToString("D"),
                caller is null ? "unknown:local" : credentials!.PrincipalOf(caller), caller?.Chain ?? [],
                NotifyOwnerProtocol.Op, "owner:policy", "refused", reason)) != true) { reason = "auditUnwritable"; }
            return new(requestId, [new(OwnerClientKind.App, OwnerNotifyOutcome.Refused, reason),
                new(OwnerClientKind.Pwa, OwnerNotifyOutcome.Refused, reason)]);
        }
        if (!channel.IsOpen || caller is not { Kind: "script", NotifyOwner: true, Scope: AgentProtocol.ScopeReadReply })
        { return Refuse("callerNotAuthorized"); }
        NotifyOwnerRequest? request;
        try
        {
            if (data.ValueKind != JsonValueKind.Object) { return Refuse("badRequest"); }
            if (Utf8.GetByteCount(data.GetRawText()) > NotifyOwnerProtocol.MaxRequestBytes) { return Refuse("payloadTooLarge"); }
            if (data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                || data.EnumerateObject().Any(p => p.Name is not ("request" or "audience" or "target" or "title" or "body")))
            { return Refuse("badRequest"); }
            request = data.Deserialize<NotifyOwnerRequest>(ProtocolJson.Options);
            if (request is null || request.Request == Guid.Empty || string.IsNullOrWhiteSpace(request.Title)
                || string.IsNullOrWhiteSpace(request.Body)) { return Refuse("badRequest"); }
            requestId = request.Request;
            if (request.Audience != "owner") { return Refuse("audienceRefused"); }
            if (request.Target != "policy") { return Refuse("targetRefused"); }
            if (request.Title.Concat(request.Body).Any(c => char.IsControl(c) || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
            { return Refuse("badRequest"); }
            if (Utf8.GetByteCount(request.Title) > PushNotice.TitleBytes || Utf8.GetByteCount(request.Body) > PushNotice.BodyBytes)
            { return Refuse("payloadTooLarge"); }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or EncoderFallbackException)
        { return Refuse("badRequest"); }
        lock (gate)
        {
            var now = time.GetUtcNow();
            foreach (var old in windows.Where(w => now - w.Value.At >= TimeSpan.FromMinutes(1)).Select(w => w.Key).ToArray())
            { windows.Remove(old); }
            var principal = credentials!.PrincipalOf(caller);
            var window = windows.GetValueOrDefault(principal, (At: now, Count: 0));
            if (window.Count >= 6) { return Refuse("rateLimited"); }
            windows[principal] = (window.At, window.Count + 1);
        }
        if (sessions is null) { return Refuse("deliveryUnavailable"); }
        var intent = new AgentAuditEntry("local", requestId.ToString("D"), credentials!.PrincipalOf(caller), caller.Chain,
            NotifyOwnerProtocol.Op, "owner:policy", "attempted");
        if (audit?.Append(intent) != true) { return Refuse("auditUnwritable"); }
        var admitted = true;
        bool Authorized() => Volatile.Read(ref admitted) && credentials.IsLive(caller);
        var result = await sessions.NotifyOwnerAsync(request, Authorized);
        foreach (var ack in result.Clients)
        {
            if (audit.Append(intent with { Outcome = ack.Outcome.ToString().ToLowerInvariant(), Reason = $"{ack.Client}:{ack.Reason}" })) { continue; }
            Volatile.Write(ref admitted, false);
            return Refuse("auditUnwritable");
        }
        return result;
    }
}
