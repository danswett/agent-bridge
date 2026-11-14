using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class StopCommands
{
    public const string Usage = "agent-bridge-host stop <session> [--confirm <token>] [--profile <name>]";

    public static async Task<int> RunWithProfileAsync(IReadOnlyList<string> args, string root, Func<bool> refused, TextWriter output, TextWriter error)
    {
        int Invalid() { error.WriteLine("usage: " + Usage); return 2; }
        if (args.Count < 2 || args[0] != "stop" || string.IsNullOrWhiteSpace(args[1])
            || args[1].StartsWith("--", StringComparison.Ordinal)) { return Invalid(); }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i += 2)
        {
            if (args[i] is not ("--profile" or "--confirm") || i + 1 >= args.Count
                || !options.TryAdd(args[i], args[i + 1])) { return Invalid(); }
        }
        var profile = options.GetValueOrDefault("--profile", "default");
        var request = new StopRequest { Id = args[1], Request = Guid.NewGuid().ToString("D"), Confirm = options.GetValueOrDefault("--confirm") };
        if (!Identity.IsValidProfile(profile) || Stops.Check(request) is not null) { return Invalid(); }
        if (refused()) { error.WriteLine("agent-bridge-host: refused: stopping a session is for a person in their own terminal"); return 4; }
        try
        {
            await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile)));
            await channel.HelloAsync();
            var result = await channel.ReadAsync<StopResult>(Ops.SessionStop, request, timeout: TimeSpan.FromSeconds(30));
            if (result.Outcome == StopOutcome.Confirm && (string.IsNullOrEmpty(result.Confirm) || result.ConfirmSeconds is not > 0
                || Stops.Check(request with { Confirm = result.Confirm }) is not null))
            { throw new MalformedRecordException("$.confirm", "unreadable confirmation"); }
            output.WriteLine(JsonSerializer.Serialize(new StopResult(result.Outcome)
            {
                Confirm = result.Outcome == StopOutcome.Confirm ? result.Confirm : null,
                ConfirmSeconds = result.Outcome == StopOutcome.Confirm ? result.ConfirmSeconds : null,
                Forced = result.Outcome == StopOutcome.Ended ? result.Forced : null,
            }, ProtocolJson.Options));
            return result.Outcome switch { StopOutcome.Ended => 0, StopOutcome.Confirm => 3, StopOutcome.Refused => 4, _ => 1 };
        }
        catch (HostErrorException)
        {
            error.WriteLine("agent-bridge-host: refused: the host refused this stop request");
            return 4;
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException)
        {
            error.WriteLine("agent-bridge-host: stop result unavailable; check at this machine before trying again");
            return 1;
        }
    }
}
