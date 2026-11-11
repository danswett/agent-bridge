using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public static class AgentCommands
{
    public const string Usage = "agent-bridge-host agents adopt <session> [--scope read|read+reply] | agents revoke <session> | agents credential [--profile <name>]";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string folder, Func<bool> refused, TextWriter output, TextWriter error)
    {
        var verb = args.ElementAtOrDefault(1);
        var target = args.ElementAtOrDefault(2);
        var valid = args switch
        {
            ["agents", "credential"] => true,
            ["agents", "revoke" or "adopt", var session] => !string.IsNullOrWhiteSpace(session) && !session.StartsWith("--", StringComparison.Ordinal),
            ["agents", "adopt", var session, "--scope", var scope] => !string.IsNullOrWhiteSpace(session)
                && !session.StartsWith("--", StringComparison.Ordinal) && AgentProtocol.AdoptableScopes.Contains(scope, StringComparer.Ordinal),
            _ => false,
        };
        if (!valid) { error.WriteLine("usage: " + Usage); return 2; }
        if (verb != "credential" && refused()) { return 4; }
        if (!OperatingSystem.IsWindows()) { error.WriteLine("agent-bridge-host: adopting a session is Windows only for now"); return 2; }
        try
        {
            await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(folder));
            await channel.HelloAsync();
            if (verb == "credential")
            {
                var issued = await channel.CallAsync<AgentCredentialResult>(AgentProtocol.Credential);
                output.WriteLine(issued.Token);
                error.WriteLine($"credential for {issued.Principal}, scope {issued.Scope}, until {issued.ExpiresAt.ToLocalTime():g}. Keep it to this session.");
            }
            else if (verb == "adopt")
            {
                var adopted = await channel.CallAsync<AgentAdoptResult>(AgentProtocol.Adopt,
                    new AgentAdoptRequest { Session = target!, Scope = args.Count == 5 ? args[4] : null });
                output.WriteLine($"adopted session {adopted.Session} as {adopted.Scope}; run agent-bridge-host agents credential --profile {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))} inside it within {AgentProtocol.AdoptionMinutes} minutes.");
            }
            else
            {
                var revoked = await channel.CallAsync<AgentRevokeResult>(AgentProtocol.Revoke, new AgentRevokeRequest { Session = target! });
                output.WriteLine(revoked.Revoked ? $"session {revoked.Session} no longer acts as an agent" : $"session {revoked.Session} had no adoption or credential");
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        {
            error.WriteLine($"agent-bridge-host: {e.Message}");
            return 1;
        }
    }
}
