using System.Text.Json;
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
// version: the schema's. A file from a newer schema is not guessed at: both are off.
public sealed record HostSettings(bool RemoteApproval)
{
    public const string FileName = "config.json";
    public const int Version = 1;

    public static readonly HostSettings Default = new(RemoteApproval: true);

    public LaunchConfig Launch { get; init; } = LaunchConfig.Default;

    // What was wrong with the launch section, entry by entry; launching stays on for what was right.
    public IReadOnlyList<string> LaunchProblems { get; init; } = [];

    static HostSettings Off => Default with { RemoteApproval = false, Launch = LaunchConfig.Default with { Enabled = false } };

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
            return (Off, $"{path} could not be read ({e.Message}); remote approval of permission prompts and launching are off");
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
                    return (Off, $"{path} is version {v}, written by a newer Agent Bridge than this host (version {Version}); remote approval of permission prompts and launching are off until the host is updated");
                }
            }
            var (launch, launchProblems) = LaunchConfig.Read(root.TryGetProperty("launch", out var l) ? l : null);
            var settings = Default with { Launch = launch, LaunchProblems = launchProblems.Count == 0 ? [] : launchProblems };
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
            return (Off, $"{path} is not valid ({e.Message}); remote approval of permission prompts and launching are off");
        }
    }

    internal static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
