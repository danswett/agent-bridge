using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core.DevBox;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

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
// devBox: keeping a Microsoft Dev Box from hibernating on disconnect (DevBoxSettings,
// KeepAwakeService). Off unless turned on; a file that cannot be read leaves it off.
//
// sessions.restoreOnStart: whether sessions a reboot or a crash took are kept offered to resume,
// and later (#780) resumed, idle, when the host starts again (docs/host-launch.md). On when absent.
// A value that is not true or false, or a file that cannot be read, turns it off: nothing is started.
// What was owed is kept all the same (RestoreKept): the person may have meant it on, and a file broken
// for a while, or written by a newer host, must not drop the sessions a reboot took.
//
// permissions.escapedApproval and escapedApprovalDevices (#803): how Approve is given for an
// approval the host escaped anything in. "confirm" (the default): a two-step confirmation from a
// person's device approved here, the devices listed, or launch.remoteFolderTrustDevices while the
// list is absent. "machine": Approve only at the machine; Deny still goes from anywhere. A value
// that cannot be read is "machine", and a list that cannot be read approves no device; each is said.
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

    // The Dev Box keep-awake (docs/host-config.md): off unless turned on here.
    public DevBoxSettings DevBox { get; init; } = DevBoxSettings.Default;

    public IReadOnlyList<string> DevBoxProblems { get; init; } = [];

    // Every problem a section reported, for the log, config show, and config set's check.
    public IEnumerable<string> SectionProblems => LaunchProblems.Concat(HomeAssistantProblems).Concat(UsageProblems).Concat(DevBoxProblems).Concat(PermissionProblems);

    public bool Push { get; init; } = true;

    public bool ReadOnly { get; init; }
    public bool TrustedPeers { get; init; }

    public bool RestoreOnStart { get; init; } = true;
    // permissions.escapedApproval: EscapedApproval.Confirm or .Machine.
    public string EscapedApproval { get; init; } = AgentBridge.Protocol.EscapedApproval.Confirm;

    // permissions.escapedApprovalDevices: null while absent, when launch.remoteFolderTrustDevices
    // is used; a list, even an empty one, replaces it.
    public IReadOnlyList<string>? EscapedApprovalDevices { get; init; }

    public IReadOnlyList<string> PermissionProblems { get; init; } = [];

    // Off only because the file could not be read: what a reboot or a crash took is kept to offer.
    public bool RestoreKept { get; init; }

    // Why a read-only host takes nothing, said to devices; and why one whose file has a problem doesn't.
    public const string ReadOnlyReason = "this is a read-only comparison host (delivery.readOnly in its config); answer at the machine";
    public const string ProblemReason = "this host's config.json has a problem, so it takes nothing from other devices; answer at the machine";

    // Usage too: a file that cannot be read may have said usage.publish false, and asking vendors
    // anyway would read a setting meant as off as on.
    static HostSettings Off => Default with
    {
        RemoteApproval = false, Launch = LaunchConfig.Default with { Enabled = false }, Push = false, ReadOnly = true,
        Usage = UsageSettings.Default with { Publish = false }, TrustedPeers = true, RestoreOnStart = false, RestoreKept = true,
        EscapedApproval = AgentBridge.Protocol.EscapedApproval.Machine, EscapedApprovalDevices = [],
    };

    public static (HostSettings Settings, string? Problem) Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        string text;
        // Only "not found" is absent. File.Exists also said false for a config.json that is a folder
        // or whose metadata could not be read, which read as no settings at all: everything on.
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (Default, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (Off, $"{path} could not be read ({e.Message}); remote approval of permission prompts, launching, notifications and agent usage are off, and the host is read-only");
        }
        return Parse(text, path);
    }

    // The file's text as Load reads it, path only naming it in what is said: config set checks a
    // change with this before anything is written, so a value it refuses (a token pasted where a
    // login belongs) never reaches the disk.
    public static (HostSettings Settings, string? Problem) Parse(string text, string path)
    {
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
                    return (Off, $"{path} is version {v}, written by a newer {Product.Name} than this host (version {Version}); remote approval of permission prompts, launching, notifications and agent usage are off, and the host is read-only, until the host is updated");
                }
            }
            var (launch, launchProblems) = LaunchConfig.Read(root.TryGetProperty("launch", out var l) ? l : null);
            var (homeAssistant, homeAssistantProblems) = HaSettings.Read(root.TryGetProperty("homeAssistant", out var h) ? h : null);
            var (usage, usageProblems) = UsageSettings.Read(root.TryGetProperty("usage", out var u) ? u : null);
            var (devBox, devBoxProblems) = DevBoxSettings.Read(root.TryGetProperty("devBox", out var d) ? d : null);
            var settings = Default with
            {
                Launch = launch,
                LaunchProblems = launchProblems.Count == 0 ? [] : launchProblems,
                HomeAssistant = homeAssistant,
                HomeAssistantProblems = homeAssistantProblems.Count == 0 ? [] : homeAssistantProblems,
                Usage = usage,
                UsageProblems = usageProblems.Count == 0 ? [] : usageProblems,
                DevBox = devBox,
                DevBoxProblems = devBoxProblems.Count == 0 ? [] : devBoxProblems,
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
            if (root.TryGetProperty("sessions", out var sessions) && sessions.ValueKind != JsonValueKind.Null)
            {
                if (sessions.ValueKind != JsonValueKind.Object) { throw new JsonException("sessions is not an object"); }
                if (sessions.TryGetProperty("restoreOnStart", out var restore) && restore.ValueKind != JsonValueKind.Null)
                {
                    settings = settings with
                    {
                        RestoreOnStart = restore.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => throw new JsonException("sessions.restoreOnStart is not true or false"),
                        },
                    };
                }
            }
            if (!root.TryGetProperty("permissions", out var permissions)) { return (settings, null); }
            if (permissions.ValueKind != JsonValueKind.Object) { throw new JsonException("permissions is not an object"); }
            settings = Escaped(settings, permissions);
            if (permissions.TryGetProperty("trustedPeers", out var trusted))
            {
                settings = settings with
                {
                    TrustedPeers = trusted.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new JsonException("permissions.trustedPeers is not true or false"),
                    },
                };
            }
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
            return (Off, $"{path} is not valid ({e.Message}); remote approval of permission prompts, launching, notifications and agent usage are off, and the host is read-only");
        }
    }

    // permissions.escapedApproval and escapedApprovalDevices, read in the direction that does least:
    // what is wrong with either is said, and never turns on more than was meant.
    static HostSettings Escaped(HostSettings settings, JsonElement permissions)
    {
        var problems = new List<string>();
        if (permissions.TryGetProperty("escapedApproval", out var policy) && policy.ValueKind != JsonValueKind.Null)
        {
            var value = policy.ValueKind == JsonValueKind.String ? policy.GetString() : null;
            if (value is AgentBridge.Protocol.EscapedApproval.Confirm or AgentBridge.Protocol.EscapedApproval.Machine) { settings = settings with { EscapedApproval = value }; }
            else
            {
                settings = settings with { EscapedApproval = AgentBridge.Protocol.EscapedApproval.Machine };
                problems.Add($"permissions.escapedApproval is not \"{AgentBridge.Protocol.EscapedApproval.Confirm}\" or \"{AgentBridge.Protocol.EscapedApproval.Machine}\"; an approval with escaped characters is approved only at the machine");
            }
        }
        if (permissions.TryGetProperty("escapedApprovalDevices", out var devices) && devices.ValueKind != JsonValueKind.Null)
        {
            if (devices.ValueKind != JsonValueKind.Array)
            {
                settings = settings with { EscapedApprovalDevices = [] };
                problems.Add("permissions.escapedApprovalDevices is not a list of device ids; no device may approve one with escaped characters");
            }
            else
            {
                var ids = new List<string>();
                foreach (var e in devices.EnumerateArray())
                {
                    var id = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if (TransferProtocol.IsDeviceId(id)) { if (!ids.Contains(id!) && ids.Count < 64) { ids.Add(id!); } }
                    else { problems.Add($"permissions.escapedApprovalDevices: '{Limits.KeepStart(id ?? e.ValueKind.ToString(), 40)}' is not a device id; ignored"); }
                }
                settings = settings with { EscapedApprovalDevices = ids };
            }
        }
        return problems.Count == 0 ? settings : settings with { PermissionProblems = problems };
    }

    internal static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
