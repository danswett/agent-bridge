using System.Text.Json;

namespace AgentBridge.Host.Core.AgentMessaging;

// config.json's agentMessaging section (docs/host-config.md): the temporary "AP5-lite" mode (#728),
// in which the owner's own agents message each other host to host over the relay before the signed
// roster (#55) exists. On by default since the owner's decision of Nov 19, 2026, 22:07 PT, but only
// as far as LiteChannel.Untrusted lets it: a host with no signed-in account, or no present
// same-account peer, still sends and receives nothing. This holds only the settings; nothing here
// sends, receives or signs.
//
//   remote           "own-account" (the default when the key is absent or null) or "off"
//   allowAllTargets  false (the default) or true: whether own-agent text may enter an allow-all
//                    session. Counts only while remote is explicitly "own-account", so the default
//                    never opens one; otherwise such a target falls back to Home Assistant.
//   allowAllTargetSessions [] (default): exact session ids allowed with allowAllTargets on; names and wildcards grant nothing.
//   allowAllLaunchedTargets false (default): all live allow-all launches here, independently of the list path.
//   laneDelivery     false (default): the owner-gated lane delivery path (docs/message-delivery.md). Counts only while remote is
//                    explicitly "own-account"; a consumer must also AND it with liteKill.On. Nothing reads it yet.
//
// Only a clean absent key is on. An explicit "off" (which the kill writes), an unknown value, a
// section that is not an object, any problem in a section without remote, or a file that cannot
// be read (Closed) is off. Invalid list entries are ignored. Installs/upgrades never write these keys.
//
// A change needs no restart: HostSettings.CurrentAgentMessaging(folder) is called per message, which reads
// the file afresh, so switching to off holds from the next message and loses none (a message
// that is not sent over the relay goes over Home Assistant).
public sealed record AgentMessagingSettings
{
    public const string Off = "off";
    public const string OwnAccount = "own-account";

    public static readonly AgentMessagingSettings Default = new() { Remote = OwnAccount };

    // What a section or a config.json that cannot be read means.
    public static readonly AgentMessagingSettings Closed = new();

    public string Remote { get; init; } = Off;
    // Whether config.json names remote itself. After a kill, the default alone never turns the mode back on.
    public bool RemoteSet { get; init; }
    public bool AllowAllTargets { get; init; }
    public bool AllowAllLaunchedTargets { get; init; }
    public bool LaneDelivery { get; init; }
    public bool ClaudeBusyQueue { get; init; }
    public IReadOnlyList<string> AllowAllTargetSessions { get; init; } = [];

    public bool RemoteEnabled => Remote == OwnAccount;

    public static (AgentMessagingSettings Settings, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        var problems = new List<string>();
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Default, problems); }
        if (s.ValueKind != JsonValueKind.Object)
        {
            problems.Add("agentMessaging is not an object, so remote agent messaging is off");
            return (Closed, problems);
        }
        var settings = Default;
        if (s.TryGetProperty("remote", out var remote) && remote.ValueKind != JsonValueKind.Null)
        {
            var value = remote.ValueKind == JsonValueKind.String ? remote.GetString() : null;
            settings = settings with { Remote = value == OwnAccount ? OwnAccount : Off, RemoteSet = true };
            if (value is not (OwnAccount or Off)) { problems.Add($"agentMessaging.remote is not \"{Off}\" or \"{OwnAccount}\", so remote agent messaging is off"); }
        }
        if (s.TryGetProperty("allowAllTargets", out var all) && all.ValueKind != JsonValueKind.Null)
        {
            if (all.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { AllowAllTargets = all.ValueKind == JsonValueKind.True }; }
            else { problems.Add("agentMessaging.allowAllTargets is not true or false, so own agents may not type into allow-all sessions"); }
        }
        if (s.TryGetProperty("allowAllLaunchedTargets", out var launched))
        {
            if (launched.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { AllowAllLaunchedTargets = launched.ValueKind == JsonValueKind.True }; }
            else { problems.Add("agentMessaging.allowAllLaunchedTargets is not true or false, so the all-launched grant is off"); }
        }
        if (s.TryGetProperty("laneDelivery", out var lane))
        {
            if (lane.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { LaneDelivery = lane.ValueKind == JsonValueKind.True }; }
            else { problems.Add("agentMessaging.laneDelivery is not true or false, so lane delivery is off"); }
        }
        if (s.TryGetProperty("claudeBusyQueue", out var busyQueue))
        {
            if (busyQueue.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { ClaudeBusyQueue = busyQueue.ValueKind == JsonValueKind.True }; }
            else { problems.Add("agentMessaging.claudeBusyQueue is not true or false, so busy sends to Claude stay refused"); }
        }
        if (s.TryGetProperty("allowAllTargetSessions", out var targets) && targets.ValueKind != JsonValueKind.Null)
        {
            if (targets.ValueKind != JsonValueKind.Array) { problems.Add("agentMessaging.allowAllTargetSessions is not a list, so no allow-all sessions are enabled"); }
            else
            {
                var sessions = new List<string>();
                foreach (var entry in targets.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                    {
                        problems.Add("agentMessaging.allowAllTargetSessions contains an empty or non-string entry; it is ignored");
                        continue;
                    }
                    var id = entry.GetString()!;
                    if (!sessions.Contains(id, StringComparer.Ordinal)) { sessions.Add(id); }
                }
                settings = settings with { AllowAllTargetSessions = sessions };
            }
        }
        // A section with a problem may have been meant as off, so the default does not turn it on; an
        // explicit own-account keeps its list problems as before. allow-all counts only with remote set to own-account.
        if (!settings.RemoteSet && problems.Count > 0) { settings = settings with { Remote = Off }; }
        if (!settings.RemoteEnabled || !settings.RemoteSet) { settings = settings with { AllowAllTargets = false }; }
        if (!settings.RemoteEnabled || !settings.RemoteSet) { settings = settings with { AllowAllLaunchedTargets = false, LaneDelivery = false, ClaudeBusyQueue = false }; }
        return (settings, problems);
    }
}

