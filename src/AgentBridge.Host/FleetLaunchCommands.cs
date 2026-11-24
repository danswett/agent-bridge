using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class FleetLaunchCommands
{
    sealed record Remaining(int Hour, int Day, int Live);
    sealed record View(Guid? Request = null, Guid? Grant = null, long? Generation = null, string? Target = null,
        string Outcome = "refused", string? Session = null, string? SessionId = null, string? Permissions = null,
        bool Registered = false, string? Reason = "bad-argument", bool Uncertain = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Remaining? Remaining = null)
    {
        public int Version => 1;
        public string Action => "launch";
        public string Time => DateTime.UtcNow.ToString("O");
    }
    static readonly string[] Reasons = ["grant-missing", "grant-expired", "grant-revoked", "grant-exhausted", "scope",
        "name-gate", "duplicate-id", "duplicate-store-full", "target-offline", "kill-switch", "peer-not-trusted",
        "person-required", "audit-unavailable", "policy", "bad-argument", "unavailable"];

    public static Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error) =>
        RunAsync(args, root, output, error, Environment.GetEnvironmentVariable("AGENT_BRIDGE_TOKEN"), null, Console.In);
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error,
        string? token, Func<string, object?, Task<JsonElement>>? exchange, TextReader input)
    {
        var view = new View();
        var exit = 2;
        var stage = "arguments";
        var sent = false;
        try
        {
            var (launch, profile) = await ParseAsync(args, input);
            view = view with { Request = launch.Request, Grant = launch.Grant, Target = launch.Target ?? "local" };
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
                stage = LaunchProtocol.Launch;
                sent = true;
                (view, exit) = Received(launch, await exchange(LaunchProtocol.Launch, launch));
                stage = "complete";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or HostErrorException or TimeoutException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine($"fleet launch: stage={stage} error={e.GetType().Name}");
            if (stage != "complete")
            {
                var invalid = stage == "arguments" && e is JsonException or FleetLaunchDenied;
                var caller = !sent && (e is LocalRefusedException || stage == AgentProtocol.Hello && e is HostErrorException);
                exit = sent ? 7 : invalid ? 2 : caller ? 4 : 6;
                view = view with { Outcome = sent ? "unconfirmed" : invalid || caller ? "refused" : "failed",
                    Reason = invalid ? "bad-argument" : caller ? "person-required" : "unavailable", Uncertain = sent };
            }
        }
        if (exit != 0) { error.WriteLine($"fleet launch: stage={stage} reason={view.Reason}"); }
        output.WriteLine(JsonSerializer.Serialize(view, ProtocolJson.Options));
        return exit;
    }

    static async Task<(FleetLaunchInput Input, string Profile)> ParseAsync(IReadOnlyList<string> args, TextReader input)
    {
        void Invalid() => throw new FleetLaunchDenied("bad-argument");
        if (args.Count < 2 || args[0] != "fleet" || args[1] != "launch") { Invalid(); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, object?> { ["version"] = 1 };
        var profile = "default";
        string? file = null;
        for (var i = 2; i < args.Count; i++)
        {
            var flag = args[i];
            if (!seen.Add(flag)) { Invalid(); }
            if (flag == "--isolated") { values.Add("isolated", true); continue; }
            if (i + 1 >= args.Count) { Invalid(); }
            var value = args[++i];
            if (flag == "--profile") { profile = value; }
            else if (flag == "--json") { file = value; }
            else if (flag is "--request" or "--grant" or "--target" or "--name" or "--workspace" or "--agent"
                or "--model" or "--launch-profile" or "--effort" or "--context" or "--permissions" or "--prompt")
            { values.Add(flag == "--launch-profile" ? "launchProfile" : flag[2..], value); }
            else { Invalid(); }
        }
        if (!Identity.IsValidProfile(profile) || file is not null && values.Count != 1) { Invalid(); }
        JsonElement data;
        if (file is null) { data = JsonSerializer.SerializeToElement(values, ProtocolJson.Options); }
        else
        {
            var reader = file == "-" ? input : File.OpenText(file);
            try
            {
                var buffer = new char[LaunchProtocol.MaxRequest + 1];
                var count = await reader.ReadBlockAsync(buffer.AsMemory());
                if (count > LaunchProtocol.MaxRequest) { Invalid(); }
                using var document = JsonDocument.Parse(new string(buffer, 0, count));
                data = document.RootElement.Clone();
            }
            finally { if (file != "-") { reader.Dispose(); } }
        }
        var launch = FleetGrantLaunch.Parse(data);
        if (launch.Target is { } target && !(target.Length == 32 && target.All(char.IsAsciiHexDigit)
            || target.Length == 29 && target.StartsWith("agent_bridge_", StringComparison.Ordinal)
                && target[13..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))) { Invalid(); }
        return (launch, profile);
    }

    static (View View, int Exit) Received(FleetLaunchInput input, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
        { throw new JsonException("invalid receipt"); }
        var receipt = data.Deserialize<FleetLaunchReceipt>(ProtocolJson.Options);
        if (receipt is null || receipt.Request != input.Request || receipt.Grant != input.Grant || receipt.Result is null
            || !Enum.IsDefined(receipt.Result.Outcome) || receipt.Generation < 0
            || receipt.Permissions is not (null or "ask" or "allowAll")
            || receipt.Reason is { } reason && !Reasons.Contains(reason, StringComparer.Ordinal)
            || receipt.Caps is { } caps && (!GrantValidator.Positive(caps) || caps.Hourly > 1000 || caps.Daily > 5000 || caps.Live > 100)
            || receipt.Usage is { } usage && (usage.Hourly < 0 || usage.Daily < 0 || usage.Live < 0))
        { throw new JsonException("invalid receipt fields"); }
        string? session = null;
        if (receipt.Result.Session is { } full)
        {
            if ((!Guid.TryParseExact(full, "D", out var id) && !Guid.TryParseExact(full, "N", out id)) || id == Guid.Empty)
            { throw new JsonException("invalid session"); }
            session = id.ToString("D");
        }
        var confirmed = receipt.Result.Outcome == LaunchOutcome.Started && receipt.Registered;
        if (receipt.Registered && (!confirmed || session is null || receipt.Permissions is null || receipt.Generation <= 0
            || receipt.Caps is null || receipt.Usage is null || input.Permissions is not null && input.Permissions != receipt.Permissions))
        { throw new JsonException("invalid registration"); }
        var outcome = confirmed ? "started" : receipt.Result.Outcome switch
        { LaunchOutcome.Refused or LaunchOutcome.Busy => "refused", LaunchOutcome.Failed => "failed", _ => "unconfirmed" };
        var uncertain = outcome == "unconfirmed";
        var remaining = receipt.Caps is { } limit && receipt.Usage is { } spent
            ? new Remaining(Math.Max(0, limit.Hourly - spent.Hourly), Math.Max(0, limit.Daily - spent.Daily), Math.Max(0, limit.Live - spent.Live)) : null;
        return (new(input.Request, input.Grant, receipt.Generation > 0 ? receipt.Generation : null, input.Target ?? "local",
            outcome, session is null ? null : Guid.Parse(session).ToString("N")[..16], session, receipt.Permissions,
            confirmed, confirmed ? null : receipt.Reason ?? "policy", uncertain, remaining),
            confirmed ? 0 : uncertain ? 7 : receipt.Reason is "audit-unavailable" or "unavailable" ? 6 : receipt.Reason == "bad-argument" ? 2 : 5);
    }
}
