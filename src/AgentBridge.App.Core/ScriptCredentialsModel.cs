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
    // Changes only when the list does, so the page redraws its rows only then (a redraw drops keyboard focus).
    public string RowsKey { get; private set; } = "";

    // What the page says when the host's reply is not the shape it sends; never the reply, never a .NET exception message.
    public const string UnreadableReply = "The host sent a reply this page could not read.";

    // The host's rule for a script name: 1 to 64 ASCII letters, digits, '.', '_' or '-', and not '.' or '..'.
    public static bool ValidName(string? name) => name is { Length: >= 1 and <= 64 } && name is not ("." or "..")
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // The dialog text for a press on this page's own list: a name that already has a credential is rotated by an Add, so it says so.
    public string ConsentFor(string op, string name) =>
        Consent(op, name, Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))?.Live ?? 0);

    public static string Consent(string op, string name, int live) => op switch
    {
        HostOps.ScriptAdd when live > 0 => $"Rotate the credential for script \"{name}\"? A new one is written to its file now; the old one stops working after the script's first successful run with the new one.",
        HostOps.ScriptAdd => $"Create a credential for script \"{name}\"? It is written to the script's own file on this PC; you never see or copy it.",
        HostOps.ScriptRevoke => $"Revoke script \"{name}\"? Its credential, and any being rotated in, stops working at once.",
        HostOps.ScriptGrantFleet => $"Let script \"{name}\" launch worker sessions? It can only launch in workspaces you ticked for fleet workers, within the fleet caps.",
        HostOps.ScriptRevokeFleet => $"Stop script \"{name}\" from launching worker sessions?",
        _ => throw new ArgumentException("unknown script credential action", nameof(op)),
    };

    // A person's refresh (opening Settings) shows only what this read found, so an old notice does not outlive a good read.
    public async Task RefreshAsync() => Notice = await ReadRowsAsync();

    // Reads the list. Returns the notice for a failure, or null; a transport failure keeps the rows already shown.
    async Task<string?> ReadRowsAsync()
    {
        if (!Available) { Rows = []; RowsKey = ""; return null; }
        JsonElement reply;
        try { reply = await host.CallAsync(HostOps.ScriptList); }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or HostErrorException) { return e.Message; }
        try
        {
            var read = reply.GetProperty("scripts").EnumerateArray().Select(r => new ScriptCredentialRow(
                r.GetProperty("name").GetString() ?? throw new FormatException(), r.GetProperty("scope").GetString() ?? throw new FormatException(),
                r.GetProperty("expiresAt").GetDateTimeOffset(), r.GetProperty("live").GetInt32(), r.GetProperty("fleetLaunch").GetBoolean(),
                r.TryGetProperty("lastUse", out var used) && used.ValueKind == JsonValueKind.String ? used.GetDateTimeOffset() : null))
                .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            Rows = read;
            RowsKey = string.Join(Environment.NewLine, read.Select(r => $"{r.Name}|{r.Scope}|{r.ExpiresAt:O}|{r.Live}|{r.FleetLaunch}|{r.LastUse:O}"));
            return null;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException or JsonException)
        {
            Rows = []; RowsKey = "";
            return UnreadableReply;
        }
    }

    public Task AddAsync(string name) => PressAsync(HostOps.ScriptAdd, name);
    public Task RevokeAsync(string name) => PressAsync(HostOps.ScriptRevoke, name);
    public Task SetLaunchAsync(string name, bool on) => PressAsync(on ? HostOps.ScriptGrantFleet : HostOps.ScriptRevokeFleet, name);

    async Task PressAsync(string op, string name)
    {
        Notice = null;
        if (!Available) { return; }
        if (!ValidName(name)) { Notice = "A script name is 1 to 64 letters, digits, dots, dashes or underscores."; return; }
        string? pressNotice = null;
        try
        {
            var asked = await host.CallAsync(HostOps.ScriptConfirm, new { op, name });
            string? confirm;
            try { confirm = asked.GetProperty("confirm").GetString(); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { confirm = null; }
            if (string.IsNullOrEmpty(confirm)) { pressNotice = UnreadableReply; }
            else { await host.CallAsync(op, new { name, confirm }); }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or HostErrorException) { pressNotice = e.Message; }
        // The list is read again so the page shows what the host now holds; the press result (or its failure) stays the notice.
        var readNotice = await ReadRowsAsync();
        Notice = pressNotice ?? readNotice;
    }
}
