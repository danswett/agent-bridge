using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// `fleet end`: the agent-side CLI for fleet.end (docs/design/fleet-grants-cli.md). It only carries the
// caller's own agent credential to the host; the grant check, scope and audit all happen there.
public static class FleetEndCommands
{
    sealed record Remaining(int Hour, int Day, int Live);
    sealed record View(Guid? Request = null, Guid? Grant = null, long? Generation = null, string? Target = null,
        string Outcome = "refused", string? Session = null, string? SessionId = null, string? Reason = "bad-argument",
        bool Uncertain = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Remaining? Remaining = null)
    {
        public int Version => 1;
        public string Action => "end";
        public bool Registered => false;
        public string Time => DateTime.UtcNow.ToString("O");
    }
    static readonly string[] Reasons = ["grant-missing", "grant-expired", "grant-revoked", "grant-exhausted", "scope",
        "duplicate-id", "kill-switch", "person-required", "audit-unavailable", "policy", "bad-argument", "still-running", "unavailable"];

    public static Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error) =>
        RunAsync(args, root, output, error, Environment.GetEnvironmentVariable("AGENT_BRIDGE_TOKEN"), null);
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error,
        string? token, Func<string, object?, Task<JsonElement>>? exchange)
    {
        var view = new View();
        var exit = 2;
        var stage = "arguments";
        var sent = false;
        try
        {
            var (input, profile) = Parse(args);
            view = view with { Request = input.Request, Grant = input.Grant, Target = input.Target ?? "local" };
            if (string.IsNullOrWhiteSpace(token)) { view = view with { Reason = "person-required" }; exit = 4; }
            else
            {
                stage = "connect";
                await using var channel = exchange is null ? await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile))) : null;
                exchange ??= async (op, data) => op == Ops.Hello
                    ? JsonSerializer.SerializeToElement(await channel!.HelloAsync(), ProtocolJson.Options) : await channel!.CallAsync(op, data);
                stage = Ops.Hello;
                await exchange(Ops.Hello, null);
                stage = AgentProtocol.Hello;
                await exchange(AgentProtocol.Hello, new AgentHelloRequest { Token = token });
                stage = FleetStopCommands.Op;
                sent = true;
                (view, exit) = Received(input, await exchange(FleetStopCommands.Op, input));
                stage = "complete";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or HostErrorException or TimeoutException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine($"fleet end: stage={stage} error={e.GetType().Name}");
            if (stage != "complete")
            {
                var invalid = stage == "arguments" && e is JsonException or FleetLaunchDenied;
                var caller = !sent && (e is LocalRefusedException || stage == AgentProtocol.Hello && e is HostErrorException);
                exit = sent ? 7 : invalid ? 2 : caller ? 4 : 6;
                view = view with { Outcome = sent ? "unconfirmed" : invalid || caller ? "refused" : "failed",
                    Reason = invalid ? "bad-argument" : caller ? "person-required" : "unavailable", Uncertain = sent };
            }
        }
        if (exit != 0) { error.WriteLine($"fleet end: stage={stage} reason={view.Reason}"); }
        output.WriteLine(JsonSerializer.Serialize(view, ProtocolJson.Options));
        return exit;
    }

    static (FleetStopInput Input, string Profile) Parse(IReadOnlyList<string> args)
    {
        void Invalid() => throw new FleetLaunchDenied("bad-argument");
        if (args.Count < 2 || args[0] != "fleet" || args[1] != "end") { Invalid(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>();
        var profile = "default";
        for (var i = 2; i < args.Count; i++)
        {
            var flag = args[i];
            if (!seen.Add(flag) || i + 1 >= args.Count) { Invalid(); }
            var value = args[++i];
            if (flag == "--profile") { profile = value; }
            else if (flag is "--request" or "--grant" or "--session" or "--target") { values[flag[2..]] = value; }
            else { Invalid(); }
        }
        values.TryGetValue("request", out var request);
        values.TryGetValue("grant", out var grant);
        values.TryGetValue("session", out var session);
        values.TryGetValue("target", out var target);
        if (!Identity.IsValidProfile(profile) || !Guid.TryParseExact(request, "D", out var requestId) || requestId == Guid.Empty
            || !Guid.TryParseExact(grant, "D", out var grantId) || grantId == Guid.Empty
            || !(FleetSessionId.IsCanonical(session) || session is { Length: 16 } && session.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            || target is not null && !(target.Length == 32 && target.All(char.IsAsciiHexDigit)
                || target.Length == 29 && target.StartsWith("agent_bridge_", StringComparison.Ordinal)
                    && target[13..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
        { throw new FleetLaunchDenied("bad-argument"); }
        return (new FleetStopInput(1, requestId, grantId, session!, target), profile);
    }

    static (View View, int Exit) Received(FleetStopInput input, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
        { throw new JsonException("invalid receipt"); }
        var receipt = data.Deserialize<FleetStopReceipt>(ProtocolJson.Options);
        if (receipt is null || receipt.Request != input.Request || receipt.Grant != input.Grant || receipt.Generation < 0
            || receipt.Outcome is not ("ended" or "refused" or "unconfirmed")
            || receipt.Outcome == "ended" != (receipt.Reason is null)
            || receipt.Reason is { } reason && !Reasons.Contains(reason, StringComparer.Ordinal)
            || receipt.Caps is { } caps && (!GrantValidator.Positive(caps) || caps.Hourly > 1000 || caps.Daily > 5000 || caps.Live > 100)
            || receipt.Usage is { } usage && (usage.Hourly < 0 || usage.Daily < 0 || usage.Live < 0))
        { throw new JsonException("invalid receipt fields"); }
        string? session = null;
        if (receipt.SessionId is { } full)
        {
            if ((!Guid.TryParseExact(full, "D", out var id) && !Guid.TryParseExact(full, "N", out id)) || id == Guid.Empty)
            { throw new JsonException("invalid session"); }
            session = id.ToString("D");
        }
        var uncertain = receipt.Outcome == "unconfirmed";
        var remaining = receipt.Caps is { } limit && receipt.Usage is { } spent
            ? new Remaining(Math.Max(0, limit.Hourly - spent.Hourly), Math.Max(0, limit.Daily - spent.Daily), Math.Max(0, limit.Live - spent.Live)) : null;
        return (new(input.Request, input.Grant, receipt.Generation > 0 ? receipt.Generation : null, input.Target ?? "local",
            receipt.Outcome, session is null ? null : Guid.Parse(session).ToString("N")[..16], session, receipt.Reason, uncertain, remaining),
            receipt.Outcome == "ended" ? 0 : uncertain ? 7 : receipt.Reason is "audit-unavailable" or "unavailable" ? 6 : receipt.Reason == "bad-argument" ? 2 : 5);
    }
}
