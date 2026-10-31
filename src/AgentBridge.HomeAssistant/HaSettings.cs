using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.HomeAssistant;

// The connector's settings: the homeAssistant section of the profile's config.json
// (docs/host-config.md, docs/ha-connector.md). Off unless a person turns it on at the machine.
//
//   "homeAssistant": {
//     "enabled": false,
//     "baseUrl": "http://homeassistant.local:8123",
//     "dashboardUrlPath": "agent-decisions",
//     "detailedActivity": true,
//     "machineName": null,
//     "disposable": false,
//     "notifications": { "enabled": false, "service": "notify.notify", "tickerCategory": "" },
//     "answerButtons": { "enabled": true, "services": [], "maxButtons": 6, "icon": "mdi:chat-question",
//                        "iconColor": "#FFFFFF", "color": "#FF9F0A", "interruptionLevel": "time-sensitive" }
//   }
//
// The token is never here: it is kept beside the device key, protected (HaTokenStore).
public sealed record HaSettings
{
    public const string DefaultDashboard = "agent-decisions";

    public bool Enabled { get; init; }
    public Uri? BaseUrl { get; init; }
    // The dashboard agent-ha-bridge's publication writer generates, read (never written) to
    // know when an ended session's card has gone (dashboard.urlPath there).
    public string DashboardUrlPath { get; init; } = DefaultDashboard;
    // What a card carries when the machine's Detailed activity switch does not exist
    // (detailedActivity there).
    public bool DetailedActivity { get; init; } = true;
    // The machine name the contract is keyed on. agent-ha-bridge uses the computer's name, so
    // this is only for a rehearsal that must pose as another machine on a disposable HA.
    public string? MachineName { get; init; }
    // Says the Home Assistant at BaseUrl is a disposable one. A development build connects to
    // no other (design G6: no writes to the production HA from development builds).
    public bool Disposable { get; init; }
    // Home Assistant user ids that are agents, not people (agentUserIds there): a press made by
    // one of them is the agent's, and the card's edge says so.
    public IReadOnlyList<string> AgentUserIds { get; init; } = [];
    // Phone notifications through Home Assistant (HaNotifySettings): agent-ha-bridge's notifications
    // and answerButtons, under the same names.
    public HaNotifySettings Notify { get; init; } = HaNotifySettings.Default;

    public static readonly HaSettings Default = new();

    // Reads the section; a value that is wrong turns the connector off and says why, so a
    // setting someone may have meant as off is never read as on.
    public static (HaSettings Settings, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Default, []); }
        if (s.ValueKind != JsonValueKind.Object) { return (Default, ["homeAssistant is not an object; the Home Assistant connector is off"]); }
        var problems = new List<string>();
        bool Bool(string name, bool fallback)
        {
            if (!s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return fallback; }
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) { return v.GetBoolean(); }
            problems.Add($"homeAssistant.{name} is not true or false");
            return fallback;
        }
        string? Text(string name)
        {
            if (!s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return null; }
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) { return v.GetString()!.Trim(); }
            problems.Add($"homeAssistant.{name} is not text");
            return null;
        }
        var settings = new HaSettings
        {
            Enabled = Bool("enabled", false),
            DetailedActivity = Bool("detailedActivity", true),
            Disposable = Bool("disposable", false),
            DashboardUrlPath = Text("dashboardUrlPath") ?? DefaultDashboard,
            MachineName = Text("machineName"),
            Notify = HaNotifySettings.Read(s, problems),
        };
        if (s.TryGetProperty("agentUserIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
        {
            if (ids.ValueKind == JsonValueKind.Array && ids.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String))
            {
                settings = settings with { AgentUserIds = [.. ids.EnumerateArray().Select(i => i.GetString()!.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal)] };
            }
            else { problems.Add("homeAssistant.agentUserIds is not a list of user ids"); }
        }
        if (Text("baseUrl") is { } url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo))
            {
                settings = settings with { BaseUrl = uri };
            }
            else { problems.Add("homeAssistant.baseUrl is not an http or https address (without credentials in it)"); }
        }
        if (settings.Enabled && settings.BaseUrl is null && !problems.Any(p => p.Contains("baseUrl", StringComparison.Ordinal)))
        {
            problems.Add("homeAssistant.enabled is true but homeAssistant.baseUrl is not set");
        }
        if (settings.DashboardUrlPath.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            problems.Add("homeAssistant.dashboardUrlPath is not a dashboard path (letters, digits, '-' and '_')");
        }
        if (problems.Count > 0)
        {
            return (settings with { Enabled = false }, [.. problems.Select(p => p + "; the Home Assistant connector is off")]);
        }
        return (settings, []);
    }
}

// Whether the connector may start on this host at all, and if not, why, in words for the log and
// `agent-bridge-host ha status`. Checked before anything is sent.
public static class HaGate
{
    public static string? WhyNot(HaSettings settings, bool devBuild, bool tokenStored, bool canCheckAgentHaBridge)
    {
        if (!settings.Enabled) { return "off (homeAssistant.enabled is not true)"; }
        if (settings.BaseUrl is null) { return "off: homeAssistant.baseUrl is not set"; }
        if (devBuild && !settings.Disposable)
        {
            return "off: this is a development build, which only ever writes to a disposable Home Assistant; set homeAssistant.disposable to true for one (design G6)";
        }
        if (!tokenStored) { return "off: no Home Assistant token; give it one with: agent-bridge-host ha token set"; }
        // Not knowing whether agent-ha-bridge runs is not permission to publish its entities.
        if (!canCheckAgentHaBridge) { return "off: whether agent-ha-bridge runs for this user cannot be checked on this platform yet"; }
        return null;
    }
}

// The Home Assistant token, kept the way the host keeps its device key: DPAPI, to this Windows
// user, in the profile's folder; elsewhere, until the Keychain comes (design P5), a file only
// its owner can read. Never in config.json, never in a log. agent-ha-bridge's own token
// (~/.agent-ha-bridge) is never read: a person gives this host its own.
public static class HaTokenStore
{
    const string DpapiFile = "ha-token.dpapi";
    const string PlainFile = "ha-token";
    static readonly byte[] Entropy = "agent-bridge-ha-token-v1"u8.ToArray();

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, DpapiFile)) || File.Exists(Path.Combine(folder, PlainFile));

    public static void Save(string folder, string token)
    {
        token = token.Trim();
        if (token.Length == 0) { throw new ArgumentException("the token is empty", nameof(token)); }
        Directory.CreateDirectory(folder);
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteReplacing(Path.Combine(folder, DpapiFile), Protect(bytes));
                return;
            }
            WriteOwnerOnly(Path.Combine(folder, PlainFile), bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string? Load(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(folder, DpapiFile);
            if (!File.Exists(path)) { return null; }
            var bytes = Unprotect(File.ReadAllBytes(path));
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var plain = Path.Combine(folder, PlainFile);
        return File.Exists(plain) ? File.ReadAllText(plain).Trim() : null;
    }

    public static void Clear(string folder)
    {
        File.Delete(Path.Combine(folder, DpapiFile));
        File.Delete(Path.Combine(folder, PlainFile));
    }

    [SupportedOSPlatform("windows")]
    static byte[] Protect(byte[] bytes) => ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    static byte[] Unprotect(byte[] bytes) => ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);

    static void WriteReplacing(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    [UnsupportedOSPlatform("windows")]
    static void WriteOwnerOnly(string path, byte[] bytes)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = ownerOnly }))
        {
            fs.Write(bytes);
        }
        File.SetUnixFileMode(path, ownerOnly);
    }
}
