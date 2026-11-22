using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
namespace AgentBridge.Host;
public static class LaunchStatusCommands
{
    public static Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error) =>
        RunAsync(args, root, output, error, async (endpoint, op, data) =>
        {
            await using var channel = await LocalClient.ConnectAsync(endpoint);
            await channel.HelloAsync();
            return await channel.CallAsync(op, data);
        });
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error,
        Func<LocalEndpoint, string, object, Task<JsonElement>> call)
    {
        int Invalid() { error.WriteLine("usage: agent-bridge-host launches status --request <guid> | list [--since <minutes>] [--json] [--profile <name>]"); return 2; }
        if (args.Count < 2 || args[0] != "launches" || args[1] is not ("status" or "list")) { return Invalid(); }
        var list = args[1] == "list";
        var options = new Dictionary<string, string>();
        var json = false;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--json") { if (json) { return Invalid(); } json = true; continue; }
            if (args[i] is not ("--profile" or "--request" or "--since") || i + 1 >= args.Count
                || !options.TryAdd(args[i], args[++i])) { return Invalid(); }
        }
        var profile = options.GetValueOrDefault("--profile", "default");
        var request = options.GetValueOrDefault("--request");
        var since = 15;
        if (!Identity.IsValidProfile(profile) || (list ? request is not null : !Guid.TryParseExact(request, "D", out _))
            || (!list && options.ContainsKey("--since")) || (options.TryGetValue("--since", out var value)
                && (!int.TryParse(value, out since) || since is < 0 or > 43200))) { return Invalid(); }
        var stage = "connect/request";
        try
        {
            var response = await call(LocalEndpoint.For(Path.Combine(root, profile)), list ? "launches.list" : "launches.status",
                list ? new { since } : (object)new { request = Guid.Parse(request!).ToString("D") });
            stage = "response";
            LaunchStatus Parse(JsonElement e)
            {
                if (e.ValueKind != JsonValueKind.Object || e.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                    || new[] { "request", "outcome", "session", "permissions", "registered", "time" }.Any(k => !e.TryGetProperty(k, out _))
                    || e.GetProperty("registered").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { throw new JsonException("invalid launch status shape"); }
                var r = e.Deserialize<LaunchStatus>(ProtocolJson.Options) ?? throw new JsonException("missing launch status");
                if (!LaunchStatusLedger.Valid(r, wire: true))
                { throw new JsonException("invalid launch status fields"); }
                return r;
            }
            if (list && (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() > LaunchStatusLedger.Capacity))
            { throw new JsonException("invalid launch list"); }
            var rows = list ? response.EnumerateArray().Select(Parse).ToArray() : [Parse(response)];
            if (!list && Guid.Parse(rows[0].Request) != Guid.Parse(request!)) { throw new JsonException("request id mismatch"); }
            if (json) { output.WriteLine(JsonSerializer.Serialize(list ? (object)rows : rows[0], ProtocolJson.Options)); }
            else { foreach (var r in rows) { output.WriteLine($"{r.Request} {r.Outcome} session={r.Session ?? "-"} permissions={r.Permissions ?? "-"} registered={r.Registered.ToString().ToLowerInvariant()} time={r.Time:o}"); } }
            return list ? 0 : rows[0].Outcome switch { "started" when rows[0].Registered => 0, "unknown" => 3, "failed" or "refused" => 5, _ => 7 };
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or HostErrorException or LocalRefusedException or InvalidOperationException)
        { error.WriteLine($"launches unavailable: stage={stage} reason={e.GetType().Name}: {e.Message}"); return 6; }
    }
}
