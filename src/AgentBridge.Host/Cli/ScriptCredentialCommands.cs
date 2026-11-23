using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.Cli;

public static class ScriptCredentialCommands
{
    public const string Usage = "agent-bridge-host scripts add|revoke|grant-fleet|revoke-fleet <name> [--profile <name>]";

    public static object Handle(IProtocolChannel channel, string op, JsonElement data, ICallerGuard callers,
        AgentCredentials? credentials, AgentAuditLog? audit)
    {
        // The person's host command line (until the app path replaces it), or the local app's press. An app press also needs the
        // host's confirmation bound to the action, the script and the current state (grants G4/G7b/G8 pattern, ruling A).
        var kind = callers.Classify(channel, fresh: true).Kind;
        if (kind is not (CallerKind.HostCli or CallerKind.App) || credentials?.IsCredentialed(channel) == true)
        { throw new InvalidOperationException("refused: only a person at the machine, using the host command line or the app, may change script credentials"); }
        if (credentials is null || credentials.Problem is not null)
        { throw new InvalidOperationException("refused: script credentials are unavailable on this host"); }
        if (op == "scripts.list")
        {
            return new { Scripts = credentials.ListScripts().Select(i => new { i.Name, i.Scope, i.ExpiresAt, i.Live, i.FleetLaunch }).ToArray() };
        }
        if (op == "scripts.confirm")
        {
            var asked = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            if (asked is not ("scripts.add" or "scripts.revoke" or "scripts.grant-fleet" or "scripts.revoke-fleet") || !TryName(data, out var forName))
            { throw new ArgumentException("scripts.confirm needs a script credential operation and a valid script name"); }
            return new { Name = forName, Op = asked, Confirm = credentials.IssueScriptConfirmation(asked, forName, channel) };
        }
        if (!TryName(data, out var name))
        { throw new ArgumentException("scripts add or revoke needs a valid script name"); }
        if (op is not ("scripts.add" or "scripts.revoke" or "scripts.grant-fleet" or "scripts.revoke-fleet"))
        { throw new ArgumentException("unknown script credential operation"); }
        // The audit attempt and the change run as one step. An app press uses up its confirmation and does the step under the store's
        // lock, so a second press for the same state (a double click) finds the state already changed and is refused.
        object Change()
        {
            // Record admission before mutation: an unavailable audit must never allow an unrecorded credential write.
            if (audit?.Append(new AgentAuditEntry("local", Guid.NewGuid().ToString("D"), "person:local", [],
                op, name, "attempted")) != true)
            { throw new InvalidOperationException("refused: the audit log could not be written; no script credential was changed"); }
            if (op is "scripts.grant-fleet" or "scripts.revoke-fleet")
            {
                credentials.SetFleetLaunch(name, op == "scripts.grant-fleet");
                return new { Name = name, Grant = "launch:fleet", Enabled = op == "scripts.grant-fleet" };
            }
            if (op == "scripts.add")
            {
                var (credential, path) = credentials.MintScript(name);
                return new { credential.Id, credential.Principal, credential.Scope, credential.ExpiresAt, Path = path };
            }
            if (!credentials.RevokeScript(name)) { throw new InvalidOperationException("no script credential exists for that name"); }
            return new { Name = name, Revoked = true };
        }
        if (kind != CallerKind.App) { return Change(); }
        var sent = data.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return credentials.RunScriptPress(sent, op, name, channel, Change);
    }

    static bool TryName(JsonElement data, out string name)
    {
        name = "";
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("name", out var field)
            || field.ValueKind != JsonValueKind.String || !AgentCredentials.ValidScriptPart(field.GetString())) { return false; }
        name = field.GetString()!;
        return true;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string root, TextWriter output, TextWriter error)
    {
        var profile = args.Count == 5 && args[3] == "--profile" ? args[4] : "default";
        if (args.Count is not (3 or 5) || args[0] != "scripts" || args[1] is not ("add" or "revoke" or "grant-fleet" or "revoke-fleet")
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
