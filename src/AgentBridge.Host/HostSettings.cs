using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core.Usage;
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
// delivery.readOnly: a host that only shows sessions, and takes no answer, reply, stop, launch,
// approval or transfer from another device, whatever agent-ha-bridge is doing (ReadOnlyGate). Off
// when absent; on for the shadow host (docs/host-shadow.md). A value that is not true or false,
// or a file that cannot be read, turns it on.
//
// push.enabled: whether this machine sends Web Push notifications to the account's phones (on
// when absent). Off for a machine that should be seen but never heard from, as the shadow host
// beside agent-ha-bridge is (docs/host-shadow.md). A value that is not true or false, or a file
// that cannot be read, turns it off.
//
// usage: how agents' allowances are read (UsageSettings): whether at all, the pace, and further
// Copilot accounts. What is wrong is said, and read in the direction that does least.
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

    // Agent usage (docs/host-config.md): on at agent-ha-bridge's pace with no further account,
    // unless this section says otherwise.
    public UsageSettings Usage { get; init; } = UsageSettings.Default;

    public IReadOnlyList<string> UsageProblems { get; init; } = [];

    // Every problem a section reported, for the log, config show, and config set's check.
    public IEnumerable<string> SectionProblems => LaunchProblems.Concat(HomeAssistantProblems).Concat(UsageProblems);

    public bool Push { get; init; } = true;

    public bool ReadOnly { get; init; }

    // Why a read-only host takes nothing, said to devices; and why one whose file has a problem doesn't.
    public const string ReadOnlyReason = "this is a read-only comparison host (delivery.readOnly in its config); answer at the machine";
    public const string ProblemReason = "this host's config.json has a problem, so it takes nothing from other devices; answer at the machine";

    static HostSettings Off => Default with { RemoteApproval = false, Launch = LaunchConfig.Default with { Enabled = false }, Push = false, ReadOnly = true };

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
            return (Off, $"{path} could not be read ({e.Message}); remote approval of permission prompts, launching and notifications are off, and the host is read-only");
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
                    return (Off, $"{path} is version {v}, written by a newer Agent Bridge than this host (version {Version}); remote approval of permission prompts, launching and notifications are off, and the host is read-only, until the host is updated");
                }
            }
            var (launch, launchProblems) = LaunchConfig.Read(root.TryGetProperty("launch", out var l) ? l : null);
            var (homeAssistant, homeAssistantProblems) = HaSettings.Read(root.TryGetProperty("homeAssistant", out var h) ? h : null);
            var (usage, usageProblems) = UsageSettings.Read(root.TryGetProperty("usage", out var u) ? u : null);
            var settings = Default with
            {
                Launch = launch,
                LaunchProblems = launchProblems.Count == 0 ? [] : launchProblems,
                HomeAssistant = homeAssistant,
                HomeAssistantProblems = homeAssistantProblems.Count == 0 ? [] : homeAssistantProblems,
                Usage = usage,
                UsageProblems = usageProblems.Count == 0 ? [] : usageProblems,
            };
            if (root.TryGetProperty("delivery", out var delivery) && delivery.ValueKind != JsonValueKind.Null)
            {
                if (delivery.ValueKind != JsonValueKind.Object) { throw new JsonException("delivery is not an object"); }
                if (delivery.TryGetProperty("readOnly", out var readOnly) && readOnly.ValueKind != JsonValueKind.Null)
                {
                    settings = settings with
                    {
                        ReadOnly = readOnly.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw new JsonException("delivery.readOnly is not true or false"),
                        },
                    };
                }
            }
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
            return (Off, $"{path} is not valid ({e.Message}); remote approval of permission prompts, launching and notifications are off, and the host is read-only");
        }
    }

    internal static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
