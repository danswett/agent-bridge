using System.Globalization;
using System.Text.Json;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// What Agency adds: its profiles, and the sessions it knows can be resumed. Both are asked of
// Agency itself, as agent-ha-bridge asks (Get-BridgeAgencyProfileList,
// Get-BridgeAgencySessionEntries), and kept for a while, since each is a process start.
public sealed class AgencyCatalog(ICommandRunner runner, TimeProvider? time = null)
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly SemaphoreSlim gate = new(1, 1);
    (string Exe, DateTimeOffset At, IReadOnlyList<string>? Names)? profiles;
    (string Exe, DateTimeOffset At, IReadOnlyList<ResumeEntry> Entries)? sessions;

    // Every profile Agency lists, or null when it could not be asked. Kept 10 minutes.
    public async Task<IReadOnlyList<string>?> ProfilesAsync(string agency, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (profiles is { } p && p.Exe == agency && time.GetUtcNow() - p.At < TimeSpan.FromMinutes(10)) { return p.Names; }
            IReadOnlyList<string>? names = null;
            foreach (var args in new[] { new[] { "config", "profiles" }, ["config", "get", "profiles"] })
            {
                var r = await runner.RunAsync(agency, args, TimeSpan.FromSeconds(20), ct);
                if (r.Ran && !r.TimedOut && r.ExitCode == 0 && ParseProfiles(r.Output) is { } found) { names = found; break; }
            }
            // A failure is kept for a minute, not ten: Agency may have been mid-update.
            profiles = (agency, names is null ? time.GetUtcNow() - TimeSpan.FromMinutes(9) : time.GetUtcNow(), names);
            return names;
        }
        finally { gate.Release(); }
    }

    // Agency's resumable sessions (`agency hub list-local-sessions --json`, can_resume only).
    // Kept a minute; empty when it could not be asked.
    public async Task<IReadOnlyList<ResumeEntry>> SessionsAsync(string agency, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (sessions is { } s && s.Exe == agency && time.GetUtcNow() - s.At < TimeSpan.FromMinutes(1)) { return s.Entries; }
            var r = await runner.RunAsync(agency, ["hub", "list-local-sessions", "--json"], TimeSpan.FromSeconds(20), ct);
            var entries = r.Ran && !r.TimedOut && r.ExitCode == 0 ? ParseSessions(r.Output) : [];
            sessions = (agency, time.GetUtcNow(), entries);
            return entries;
        }
        finally { gate.Release(); }
    }

    // A "Profiles:" heading, then one indented name a line; anything with a colon is not a name
    // (each profile's MCPs and plugins are listed under it as "MCPs: …"). "No profiles
    // configured." has no heading, and means none.
    internal static IReadOnlyList<string>? ParseProfiles(string output)
    {
        if (output.Contains("No profiles configured", StringComparison.OrdinalIgnoreCase)) { return []; }
        var lines = output.Replace("\r", "").Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim().Equals("Profiles:", StringComparison.Ordinal));
        if (at < 0) { return null; }
        var names = new List<string>();
        foreach (var line in lines.Skip(at + 1))
        {
            if (line.Trim().Length == 0) { continue; }
            if (!char.IsWhiteSpace(line[0])) { break; }
            var name = line.Trim();
            if (name.Contains(':') || names.Contains(name)) { continue; }
            names.Add(name);
        }
        return names;
    }

    internal static IReadOnlyList<ResumeEntry> ParseSessions(string output)
    {
        // Agency prints a banner and a log path before the payload.
        var start = output.IndexOf('{');
        if (start < 0) { return []; }
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            var list = new List<ResumeEntry>();
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("sessions", out var items) || items.ValueKind != JsonValueKind.Array) { return []; }
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { continue; }
                if (!item.TryGetProperty("can_resume", out var can) || can.ValueKind != JsonValueKind.True) { continue; }
                var id = Str(item, "session_id");
                if (id is null || !Guid.TryParseExact(id, "D", out _)) { continue; }
                DateTimeOffset? updated = Str(item, "updated_at") is { } u && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                list.Add(new ResumeEntry(id.ToLowerInvariant(), AgentKind.Copilot, "agency", Str(item, "summary") ?? "", Str(item, "folder"), updated));
            }
            return list;
        }
        catch (JsonException) { return []; }
    }

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

// One session that could be resumed, before it is checked against what is live and approved.
public sealed record ResumeEntry(string Id, AgentKind Agent, string Launcher, string Summary, string? Folder, DateTimeOffset? Updated);
