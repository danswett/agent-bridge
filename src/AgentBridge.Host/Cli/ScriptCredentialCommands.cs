using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.Cli;

public static class ScriptCredentialCommands
{
    public const string Usage = "agent-bridge-host scripts add|revoke <name> [--profile <name>]";

    public static object Handle(IProtocolChannel channel, string op, JsonElement data, ICallerGuard callers,
        AgentCredentials? credentials, AgentAuditLog? audit)
    {
        if (callers.Classify(channel, fresh: true).Kind != CallerKind.HostCli || credentials?.IsCredentialed(channel) == true)
        { throw new InvalidOperationException("refused: only a person at the machine using the host command line may change script credentials"); }
        if (credentials is null || credentials.Problem is not null)
        { throw new InvalidOperationException("refused: script credentials are unavailable on this host"); }
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("name", out var field)
            || field.ValueKind != JsonValueKind.String || !AgentCredentials.ValidScriptPart(field.GetString()))
        { throw new ArgumentException("scripts add or revoke needs a valid script name"); }
        var name = field.GetString()!;
        // Record admission before mutation: an unavailable audit must never allow an unrecorded credential write.
        if (audit?.Append(new AgentAuditEntry("local", Guid.NewGuid().ToString("D"), "person:local", [],
            op, name, "attempted")) != true)
        { throw new InvalidOperationException("refused: the audit log could not be written; no script credential was changed"); }
        if (op == "scripts.add")
        {
            var (credential, path) = credentials.MintScript(name);
            return new { credential.Id, credential.Principal, credential.Scope, credential.ExpiresAt, Path = path };
        }
        if (!credentials.RevokeScript(name)) { throw new InvalidOperationException("no script credential exists for that name"); }
        return new { Name = name, Revoked = true };
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error)
    {
        var profile = args.Count == 5 && args[3] == "--profile" ? args[4] : "default";
        if (args.Count is not (3 or 5) || args[0] != "scripts" || args[1] is not ("add" or "revoke")
            || !AgentCredentials.ValidScriptPart(args[2]) || !Identity.IsValidProfile(profile)
            || args.Count == 5 && args[3] != "--profile")
        { error.WriteLine($"usage: {Usage}"); return 2; }
        if (!OperatingSystem.IsWindows())
        { error.WriteLine("agent-bridge-host: script credential management is available only on Windows"); return 2; }
        try
        {
            await using var local = await LocalClient.ConnectAsync(LocalEndpoint.For(Path.Combine(root, profile)));
            await local.HelloAsync();
            var result = await local.CallAsync("scripts." + args[1], new { name = args[2] });
            output.WriteLine(result.GetRawText());
            return 0;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        {
            error.WriteLine($"agent-bridge-host scripts: {e.Message}");
            return 1;
        }
    }
}
