using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

// What this host may do on this machine, set by the person who runs it: config.json in the
// profile's folder, beside the device key. Read when serve starts; a change takes a restart.
// The schema is docs/host-config.md:
//
//   { "version": 1,
//     "permissions": { "remoteApproval": true },
//     "launch": { "workspaces": [ { "path": "C:\\repos\\app", "isolate": true } ], ... } }
//
// permissions.remoteApproval: whether Copilot's permission prompts may be approved or denied
// from another device (docs/host-answers.md). On when the file or the setting is absent. A
// file that cannot be read, or a setting that is not true or false, turns it off: a setting
// the person may have meant as off is never read as on.
//
// launch: where sessions may be launched from another device, and the machine's defaults
// (LaunchConfig, docs/host-launch.md). Nothing is approved until a person adds it here at the
// machine (agent-bridge-host workspace add). A file that cannot be read turns launching off.
//
// homeAssistant: the Home Assistant connector (HaSettings, docs/ha-connector.md). Off unless
// turned on; a file that cannot be read leaves it off.
//
// push.enabled: whether this machine sends Web Push notifications to the account's phones (on
// when absent). Off for a machine that should be seen but never heard from, as the shadow host
// beside agent-ha-bridge is (docs/host-shadow.md). A value that is not true or false, or a file
// that cannot be read, turns it off.
//
// version: the schema's. A file from a newer schema is not guessed at: both are off.
public sealed record HostSettings(bool RemoteApproval)
{
    public const string FileName = "config.json";
    public const int Version = 1;

    public static readonly HostSettings Default = new(RemoteApproval: true);

    public LaunchConfig Launch { get; init; } = LaunchConfig.Default;

    // What was wrong with the launch section, entry by entry; launching stays on for what was right.
    public IReadOnlyList<string> LaunchProblems { get; init; } = [];

    // The Home Assistant connector (docs/ha-connector.md): off unless turned on here. What was
    // wrong with its section turns it off and is said, without touching anything else.
    public HaSettings HomeAssistant { get; init; } = HaSettings.Default;

    public IReadOnlyList<string> HomeAssistantProblems { get; init; } = [];

    public bool Push { get; init; } = true;

    static HostSettings Off => Default with { RemoteApproval = false, Launch = LaunchConfig.Default with { Enabled = false }, Push = false };

    public static (HostSettings Settings, string? Problem) Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        string text;
        try
        {
            if (!File.Exists(path)) { return (Default, null); }
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (Off, $"{path} could not be read ({e.Message}); remote approval of permission prompts, launching and notifications are off");
        }
        try
        {
            using var doc = JsonDocument.Parse(text, Options);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("not an object"); }
            if (root.TryGetProperty("version", out var version) && version.ValueKind != JsonValueKind.Null)
            {
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v < 1) { throw new JsonException("version is not a whole number from 1"); }
                if (v > Version)
                {
                    return (Off, $"{path} is version {v}, written by a newer Agent Bridge than this host (version {Version}); remote approval of permission prompts, launching and notifications are off until the host is updated");
                }
            }
            var (launch, launchProblems) = LaunchConfig.Read(root.TryGetProperty("launch", out var l) ? l : null);
            var (homeAssistant, homeAssistantProblems) = HaSettings.Read(root.TryGetProperty("homeAssistant", out var h) ? h : null);
            var settings = Default with
            {
                Launch = launch,
                LaunchProblems = launchProblems.Count == 0 ? [] : launchProblems,
                HomeAssistant = homeAssistant,
                HomeAssistantProblems = homeAssistantProblems.Count == 0 ? [] : homeAssistantProblems,
            };
            if (root.TryGetProperty("push", out var push) && push.ValueKind != JsonValueKind.Null)
            {
                if (push.ValueKind != JsonValueKind.Object) { throw new JsonException("push is not an object"); }
                if (push.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.Null)
                {
                    settings = settings with
                    {
                        Push = enabled.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw new JsonException("push.enabled is not true or false"),
                        },
                    };
                }
            }
            if (!root.TryGetProperty("permissions", out var permissions)) { return (settings, null); }
            if (permissions.ValueKind != JsonValueKind.Object) { throw new JsonException("permissions is not an object"); }
            if (!permissions.TryGetProperty("remoteApproval", out var remote)) { return (settings, null); }
            return remote.ValueKind switch
            {
                JsonValueKind.True => (settings, null),
                JsonValueKind.False => (settings with { RemoteApproval = false }, null),
                _ => throw new JsonException("permissions.remoteApproval is not true or false"),
            };
        }
        catch (JsonException e)
        {
            return (Off, $"{path} is not valid ({e.Message}); remote approval of permission prompts, launching and notifications are off");
        }
    }

    internal static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
