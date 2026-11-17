using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Transport;

namespace AgentBridge.Host.Cli;

public static class FleetLaunchCommands
{
    public const string Usage = "agent-bridge-host fleet status | fleet retire <request UUID> [--profile <name>]";

    internal static object Handle(IProtocolChannel channel, string op, JsonElement data, ICallerGuard callers,
        AgentCredentials? credentials, AgentAuditLog? audit, SessionService sessions)
    {
        if (callers.Classify(channel, fresh: true).Kind != CallerKind.HostCli || credentials?.IsCredentialed(channel) == true)
        { throw new InvalidOperationException("refused: only a person at the machine using the host command line may inspect or retire fleet reservations"); }
        if (audit is null) { throw new InvalidOperationException("refused: fleet admission history is unavailable"); }
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Any(p => op == "fleet.status" || p.Name != "request")
            || data.EnumerateObject().Count() != (op == "fleet.status" ? 0 : 1))
        { throw new ArgumentException("fleet status takes no fields; fleet retire needs one request UUID"); }
        var state = sessions.FleetState(audit);
        if (op == "fleet.status") { return state.Status(); }
        if (!data.TryGetProperty("request", out var value) || value.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetString(), "D", out var request) || request == Guid.Empty)
        { throw new ArgumentException("fleet retire needs a request UUID"); }
        return state.Retire(request.ToString("D"));
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error)
    {
        var rest = args.ToList();
        var profile = "default";
        if (rest.Count >= 4 && rest[^2] == "--profile")
        { profile = rest[^1]; rest.RemoveRange(rest.Count - 2, 2); }
        if (!Identity.IsValidProfile(profile) || rest.Count < 2 || rest[0] != "fleet"
            || !(rest[1] == "status" && rest.Count == 2 || rest[1] == "retire" && rest.Count == 3
                && Guid.TryParseExact(rest[2], "D", out var id) && id != Guid.Empty))
        { error.WriteLine($"usage: {Usage}"); return 2; }
        if (!OperatingSystem.IsWindows())
        { error.WriteLine("agent-bridge-host: fleet reservation management is available only on Windows"); return 2; }
        try
        {
            await using var local = await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile)));
            await local.HelloAsync();
            var data = rest[1] == "status" ? JsonSerializer.SerializeToElement(new { })
                : JsonSerializer.SerializeToElement(new { request = rest[2] });
            var result = await local.CallAsync("fleet." + rest[1], data);
            output.WriteLine(result.GetRawText());
            return 0;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        { error.WriteLine($"agent-bridge-host fleet: {e.Message}"); return 1; }
    }
}
