using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class NotifyOwnerCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error,
        Func<NotifyOwnerRequest, Task<NotifyOwnerResult>>? send = null)
    {
        var profile = args.Count == 5 && args[3] == "--profile" ? args[4] : "default";
        if (args.Count is not (3 or 5) || args[0] != NotifyOwnerProtocol.Op || !Identity.IsValidProfile(profile)
            || args.Count == 5 && args[3] != "--profile")
        { error.WriteLine("usage: agent-bridge-host notify-owner <title> <body> [--profile <name>]"); return 2; }
        var request = new NotifyOwnerRequest(Guid.NewGuid(), "owner", "policy", args[1], args[2]);
        try
        {
            var result = await (send ?? Send)(request);
            output.WriteLine(JsonSerializer.Serialize(result, ProtocolJson.Options));
            var accepted = result.Request == request.Request && result.Clients is { Count: 2 }
                && result.Clients.Select(c => c.Client).Distinct().Count() == 2
                && result.Clients.All(c => c.Client is OwnerClientKind.App or OwnerClientKind.Pwa
                    && c.Outcome is OwnerNotifyOutcome.Delivered or OwnerNotifyOutcome.Queued);
            if (!accepted) { error.WriteLine("notify-owner: refused or invalid acknowledgment; see per-client result"); }
            return accepted ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or JsonException)
        { error.WriteLine($"notify-owner: transportRefused ({e.GetType().Name})"); return 1; }

        async Task<NotifyOwnerResult> Send(NotifyOwnerRequest page)
        {
            var token = Environment.GetEnvironmentVariable("AGENT_BRIDGE_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) { throw new HostErrorException("callerNotAuthorized"); }
            await using var local = await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile)));
            await local.CallAsync<AgentHelloResult>(AgentProtocol.Hello, new AgentHelloRequest { Token = token });
            return await local.CallAsync<NotifyOwnerResult>(NotifyOwnerProtocol.Op, page);
        }
    }
}
