using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class SessionsFindCommands
{
    public const string Usage = "agent-bridge-host sessions find --session-id <ID> [--machine <device-ID>] [--json] [--profile <name>]";
    sealed record Match(string Node, string Machine, string State);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error,
        Func<LocalEndpoint, Task<LocalChannel>>? connect = null)
    {
        var rest = args.ToList();
        var p = rest.IndexOf("--profile");
        var profile = p < 0 ? "default" : rest.ElementAtOrDefault(p + 1);
        if (profile is null || !Identity.IsValidProfile(profile) || p >= 0 && rest.LastIndexOf("--profile") != p)
        { error.WriteLine("usage: " + Usage); return 2; }
        if (p >= 0) { rest.RemoveRange(p, 2); }
        if (Parse(rest) is null) { return InvalidSelector(rest, error); }
        try
        {
            var endpoint = LocalEndpoint.For(Path.Combine(root, profile));
            await using var channel = await (connect is null ? LocalClient.ConnectAsync(endpoint) : connect(endpoint));
            return await ExecuteAsync(rest, output, error, Environment.GetEnvironmentVariable("AGENT_BRIDGE_TOKEN"),
                async (op, data) => op == Ops.Hello ? JsonSerializer.SerializeToElement(await channel.HelloAsync())
                    : await channel.CallAsync(op, data));
        }
        catch (Exception e) when (Failure(e))
        { return Report("connect", e, args.Contains("--json"), output, error); }
    }

    internal static async Task<int> ExecuteAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error,
        string? token, Func<string, object?, Task<JsonElement>> request)
    {
        if (Parse(args) is not { } parsed)
        { return InvalidSelector(args, error); }
        var (sessionId, machine, json) = parsed;
        var stage = "hello";
        try
        {
            await request(Ops.Hello, null);
            stage = AgentProtocol.Hello;
            if (string.IsNullOrWhiteSpace(token)) { throw new HostErrorException("refused: no session credential in AGENT_BRIDGE_TOKEN"); }
            await request(AgentProtocol.Hello, new AgentHelloRequest { Token = token });
            stage = FleetList.FindOp;
            var reply = await request(FleetList.FindOp, new { sessionId, machine });
            stage = "fleet.find response";
            var result = Text(reply, "result");
            Text(reply, "fleetScope");
            var matches = new List<Match>();
            switch (result)
            {
                case "found": matches.Add(Read(reply.GetProperty("session"), sessionId, machine)); break;
                case "ambiguous":
                    foreach (var candidate in reply.GetProperty("candidates").EnumerateArray()) { matches.Add(Read(candidate, sessionId, machine)); }
                    if (matches.Count < 2) { throw new InvalidDataException("ambiguous requires at least two candidates"); }
                    break;
                case "notFound" or "offline": throw new HostErrorException($"refused: sessionId {JsonSerializer.Serialize(sessionId)} is {result}");
                case "unverified": throw new HostErrorException("refused: own-account routing trust is required");
                default: throw new InvalidDataException($"unexpected result: {result}");
            }
            if (json) { output.WriteLine(JsonSerializer.Serialize(new { result, matches }, ProtocolJson.Options)); }
            else
            {
                foreach (var match in matches) { output.WriteLine($"{match.Node}\t{match.Machine}\t{match.State}"); }
                if (matches.Count != 1) { error.WriteLine($"sessions find: {(matches.Count == 0 ? "notFound" : "ambiguous")}"); }
            }
            return matches.Count switch { 0 => 3, 1 => 0, _ => 4 };
        }
        catch (Exception e) when (Failure(e)) { return Report(stage, e, json, output, error); }
    }

    static (string SessionId, string? Machine, bool Json)? Parse(IReadOnlyList<string> args)
    {
        string? sessionId = null, machine = null;
        var json = false;
        if (args.ElementAtOrDefault(0) != "sessions" || args.ElementAtOrDefault(1) != "find") { return null; }
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; }
            else if (args[i] == "--session-id" && sessionId is null && i + 1 < args.Count) { sessionId = args[++i]; }
            else if (args[i] == "--machine" && machine is null && i + 1 < args.Count) { machine = args[++i]; }
            else { return null; }
        }
        return !FleetSessionMatch.IsId(sessionId) || machine is not null && !Wire.IsDeviceId(machine)
            ? null : (sessionId!, machine, json);
    }

    static int InvalidSelector(IReadOnlyList<string> args, TextWriter error)
    {
        var selector = args.ToList().FindIndex(x => x is "--session-id" or "--name");
        var id = selector >= 0 ? args.ElementAtOrDefault(selector + 1) : null;
        error.WriteLine($"refused: sessions find sessionId {JsonSerializer.Serialize(id ?? "<missing>")} requires an exclusive session ID and an exact device-ID pin; names never route");
        error.WriteLine("usage: " + Usage);
        return 2;
    }

    static Match Read(JsonElement row, string sessionId, string? pin)
    {
        var node = Text(row, "node");
        var machine = Text(row, "machine");
        var machineId = machine;
        var state = Text(row, "state");
        if (node.Length != 29 || !FleetSessionMatch.IsId(node)
            || !FleetSessionMatch.Active(state) || row.GetProperty("online").ValueKind != JsonValueKind.True)
        { throw new InvalidDataException("candidate has an invalid node or is not live"); }
        if (Text(row, "trust") != LiteFleetRoster.Trust) { throw new HostErrorException("refused: candidate is not own-account"); }
        var fullId = row.TryGetProperty("sessionId", out var published) && published.ValueKind == JsonValueKind.String ? published.GetString() : null;
        if (!FleetSessionMatch.MatchesId(node, fullId, sessionId))
        { throw new HostErrorException($"refused: host candidate does not match sessionId {JsonSerializer.Serialize(sessionId)}"); }
        if (row.TryGetProperty("machineName", out var label) && label.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(label.GetString()))
        { machine = label.GetString()!; }
        if (pin is not null && pin != machineId)
        { throw new InvalidDataException("host ignored the exact machine pin"); }
        return new(node, machine, state);
    }

    static string Text(JsonElement value, string field) =>
        value.GetProperty(field) is { ValueKind: JsonValueKind.String } text && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()! : throw new InvalidDataException($"missing or invalid {field}");

    static bool Failure(Exception e) => e is IOException or InvalidDataException or HostErrorException or TimeoutException or JsonException
        or InvalidOperationException or KeyNotFoundException or HttpRequestException;

    static int Report(string stage, Exception e, bool json, TextWriter output, TextWriter error)
    {
        var refused = e is LocalRefusedException || e is HostErrorException && e.Message.StartsWith("refused:", StringComparison.Ordinal);
        var reason = e.InnerException is { } inner ? $"{e.Message}: {inner.Message}" : e.Message;
        error.WriteLine($"sessions find: {stage}: {reason}");
        if (json) { output.WriteLine(JsonSerializer.Serialize(new { result = refused ? "refused" : "error", stage, reason }, ProtocolJson.Options)); }
        return refused ? 5 : 6;
    }
}
