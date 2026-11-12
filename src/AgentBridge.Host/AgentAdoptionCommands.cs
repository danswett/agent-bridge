using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class AgentAdoptionCommands
{
    public const string Usage = "agent-bridge-host agents adopt <session> | revoke <session> | credential [--profile <name>]";

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, Func<string, bool> refused,
        Func<string, string, object, Task<JsonElement>> call, TextWriter output, TextWriter error)
    {
        var verb = arguments.ElementAtOrDefault(1);
        var operands = arguments.Skip(2).ToList();
        var profile = "default";
        var at = operands.IndexOf("--profile");
        if (at >= 0 && at + 1 < operands.Count)
        {
            profile = operands[at + 1];
            operands.RemoveRange(at, 2);
        }
        if (!Identity.IsValidProfile(profile) || verb is not ("adopt" or "revoke" or "credential")
            || operands.Count != (verb == "credential" ? 0 : 1) || operands.Any(s => s.StartsWith("--", StringComparison.Ordinal)))
        {
            error.WriteLine("usage: " + Usage);
            return 2;
        }
        if (verb != "credential" && refused($"{verb} a session as an agent")) { return 4; }
        var op = verb == "adopt" ? AgentProtocol.Adopt : verb == "revoke" ? AgentProtocol.Revoke : AgentProtocol.Credential;
        object data = verb == "credential" ? new { } : verb == "adopt"
            ? new AgentAdoptRequest { Session = operands[0] } : new AgentRevokeRequest { Session = operands[0] };
        try
        {
            var result = await call(profile, op, data);
            if (verb == "credential")
            {
                var credential = result.Deserialize<AgentCredentialResult>(ProtocolJson.Options);
                if (credential?.Token is not { Length: AgentProtocol.TokenLength } token || !token.StartsWith(AgentProtocol.TokenPrefix, StringComparison.Ordinal))
                { throw new JsonException("the host returned an invalid credential"); }
                output.WriteLine(token);
            }
            else
            {
                if (verb == "revoke" && !result.GetProperty("revoked").GetBoolean())
                {
                    error.WriteLine("agent-bridge-host agents: no adoption or credential exists for that session");
                    return 1;
                }
                output.WriteLine(result.GetRawText());
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or JsonException or InvalidOperationException)
        {
            error.WriteLine($"agent-bridge-host agents: {e.Message}");
            return 1;
        }
    }
}
