using System.Text.Json;

namespace AgentBridge.Host.Core.AgentMessaging;

// config.json's agentMessaging section (docs/host-config.md): the temporary, off-by-default
// "AP5-lite" mode (#728), in which the owner's own agents message each other host to host
// over the relay before the signed roster (#55) exists. This holds only the settings; nothing
// here sends, receives or signs.
//
//   remote           "off" (the default) or "own-account"
//   allowAllTargets  false (the default) or true: whether own-agent text may enter an allow-all
//                    session. Matters only while remote is "own-account"; otherwise such a
//                    target falls back to Home Assistant. Not turned on anywhere.
//
// Fail closed: a value that is not exactly one of those, a section that is not an object, or a
// file that cannot be read, is off. Installing or upgrading never writes either key.
//
// A change needs no restart: later slices call HostSettings.CurrentAgentMessaging(folder) per message, which reads
// the file afresh, so switching to off holds from the next message and loses none (a message
// that is not sent over the relay goes over Home Assistant).
public sealed record AgentMessagingSettings
{
    public const string Off = "off";
    public const string OwnAccount = "own-account";

    public static readonly AgentMessagingSettings Default = new();

    public string Remote { get; init; } = Off;
    public bool AllowAllTargets { get; init; }

    public bool RemoteEnabled => Remote == OwnAccount;

    public static (AgentMessagingSettings Settings, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        var problems = new List<string>();
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Default, problems); }
        if (s.ValueKind != JsonValueKind.Object)
        {
            problems.Add("agentMessaging is not an object, so remote agent messaging is off");
            return (Default, problems);
        }
        var settings = Default;
        if (s.TryGetProperty("remote", out var remote) && remote.ValueKind != JsonValueKind.Null)
        {
            var value = remote.ValueKind == JsonValueKind.String ? remote.GetString() : null;
            if (value == OwnAccount) { settings = settings with { Remote = OwnAccount }; }
            else if (value != Off) { problems.Add($"agentMessaging.remote is not \"{Off}\" or \"{OwnAccount}\", so remote agent messaging is off"); }
        }
        if (s.TryGetProperty("allowAllTargets", out var all) && all.ValueKind != JsonValueKind.Null)
        {
            if (all.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { AllowAllTargets = all.ValueKind == JsonValueKind.True }; }
            else { problems.Add("agentMessaging.allowAllTargets is not true or false, so own agents may not type into allow-all sessions"); }
        }
        // allowAllTargets counts only in own-account mode, so it reads false otherwise.
        if (!settings.RemoteEnabled) { settings = settings with { AllowAllTargets = false }; }
        return (settings, problems);
    }
}

