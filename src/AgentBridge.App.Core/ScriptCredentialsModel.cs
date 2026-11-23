using System.Text.Json;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

public sealed record ScriptCredentialRow(string Name, string Scope, DateTimeOffset ExpiresAt, int Live, bool FleetLaunch, DateTimeOffset? LastUse);

// Settings > Script credentials (HA retirement row 1, owner option 1, ruling A). The page never holds a secret: the host writes a script's
// credential straight into its own owner-only file, and every press carries the host's confirmation for exactly that action, script and
// state, asked for just before the press (a stale or reused one is refused by the host and shown here).
public sealed class ScriptCredentialsModel(HostConnection host)
{
    public bool Available => host.Can(HostOps.ScriptCredentialsCapability);
    public IReadOnlyList<ScriptCredentialRow> Rows { get; private set; } = [];
    public string? Notice { get; private set; }

    // The host's rule for a script name: 1 to 64 ASCII letters, digits, '.', '_' or '-', and not '.' or '..'.
    public static bool ValidName(string? name) => name is { Length: >= 1 and <= 64 } && name is not ("." or "..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static string Consent(string op, string name, int live) => op switch
    {
        HostOps.ScriptAdd when live > 0 => $"Rotate the credential for script \"{name}\"? A new one is written to its file now; the old one stops working after the script's first successful run with the new one.",
        HostOps.ScriptAdd => $"Create a credential for script \"{name}\"? It is written to the script's own file on this PC; you never see or copy it.",
        HostOps.ScriptRevoke => $"Revoke script \"{name}\"? Its credential, and any being rotated in, stops working at once.",
        HostOps.ScriptGrantFleet => $"Let script \"{name}\" launch worker sessions? It can only launch in workspaces you ticked for fleet workers, within the fleet caps.",
        HostOps.ScriptRevokeFleet => $"Stop script \"{name}\" from launching worker sessions?",
        _ => throw new ArgumentException("unknown script credential action", nameof(op)),
    };

    public async Task RefreshAsync()
    {
        if (!Available) { Rows = []; return; }
        try
        {
            var reply = await host.CallAsync(HostOps.ScriptList);
            Rows = [.. reply.GetProperty("scripts").EnumerateArray().Select(r => new ScriptCredentialRow(
                r.GetProperty("name").GetString()!, r.GetProperty("scope").GetString()!, r.GetProperty("expiresAt").GetDateTimeOffset(),
                r.GetProperty("live").GetInt32(), r.GetProperty("fleetLaunch").GetBoolean(),
                r.TryGetProperty("lastUse", out var used) && used.ValueKind == JsonValueKind.String ? used.GetDateTimeOffset() : null))
                .OrderBy(r => r.Name, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or HostErrorException) { Notice = e.Message; }
    }

    public Task AddAsync(string name) => PressAsync(HostOps.ScriptAdd, name);
    public Task RevokeAsync(string name) => PressAsync(HostOps.ScriptRevoke, name);
    public Task SetLaunchAsync(string name, bool on) => PressAsync(on ? HostOps.ScriptGrantFleet : HostOps.ScriptRevokeFleet, name);

    async Task PressAsync(string op, string name)
    {
        Notice = null;
        if (!Available) { return; }
        if (!ValidName(name)) { Notice = "A script name is 1 to 64 letters, digits, dots, dashes or underscores."; return; }
        try
        {
            var asked = await host.CallAsync(HostOps.ScriptConfirm, new { op, name });
            await host.CallAsync(op, new { name, confirm = asked.GetProperty("confirm").GetString() });
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or HostErrorException) { Notice = e.Message; }
        await RefreshAsync();
    }
}
