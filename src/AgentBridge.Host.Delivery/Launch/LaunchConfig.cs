using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// The launch section of the host's config.json (docs/host-config.md): which folders a session
// may be launched in, and the machine's defaults. Nothing here is approved until a person at
// the machine adds it: with no workspaces, nothing new can be launched, and only sessions
// whose folders are approved can be resumed.
public sealed record LaunchConfig
{
    public static readonly LaunchConfig Default = new();

    // false turns launching off on this machine: hello leaves sessions.launch out.
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<WorkspaceConfig> Workspaces { get; init; } = [];
    public string? DefaultWorkspace { get; init; }
    // "auto" (the first installed, in Launchers.All's order) or a launcher id.
    public string DefaultAgent { get; init; } = "auto";
    // What a launch that does not say gets: "ask" (permission prompts) or "allowAll".
    public string Permissions { get; init; } = LaunchProtocol.Ask;
    public string WorktreeRoot { get; init; } = "~/repos/wt";
    // Managed worktrees per repository; 0 is no cap.
    public int WorktreeLimit { get; init; } = 10;
    // How long since this host made a worktree before the reclaimer may remove it once it is
    // finished (clean, detached, nothing ahead of its base, no live session in it).
    public double WorktreeIdleHours { get; init; } = 12;
    // Agency profiles to offer; empty offers every profile Agency lists.
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public string? DefaultProfile { get; init; }
    public int ResumeCount { get; init; } = 12;
    public IReadOnlyDictionary<string, AgentConfig> Agents { get; init; } = new Dictionary<string, AgentConfig>();
    // Moving ended sessions between this account's machines (docs/host-transfer.md).
    public TransferConfig Transfer { get; init; } = TransferConfig.Default;
    // Whether a person may answer a launched agent's folder-trust question from one of their
    // devices (launch.trust). Off unless a person at the machine turns it on: trusting a folder
    // lets the agent run what is in it.
    public bool RemoteFolderTrust { get; init; }
    // The devices, by id, a person approved at the machine to answer from: none until they do.
    public IReadOnlyList<string> RemoteFolderTrustDevices { get; init; } = [];

    public AgentConfig Agent(string id) => Agents.TryGetValue(id, out var a) ? a : AgentConfig.Default;

    // Reads the "launch" object. Problems are said, never thrown: an entry that is wrong is
    // left out, and a section that cannot be read turns launching off rather than guessing.
    public static (LaunchConfig Config, IReadOnlyList<string> Problems) Read(JsonElement? launch)
    {
        var problems = new List<string>();
        if (launch is not { } l || l.ValueKind == JsonValueKind.Null) { return (Default, problems); }
        if (l.ValueKind != JsonValueKind.Object) { return (Default with { Enabled = false }, ["launch is not an object; launching is off"]); }
        try
        {
            var config = Default;
            if (Get(l, "enabled") is { } enabled) { config = config with { Enabled = Bool(enabled, "launch.enabled") }; }
            if (Get(l, "workspaces") is { } workspaces) { config = config with { Workspaces = ReadWorkspaces(workspaces, problems) }; }
            if (Get(l, "defaultWorkspace") is { } dw) { config = config with { DefaultWorkspace = Text(dw, "launch.defaultWorkspace") }; }
            if (Get(l, "defaultAgent") is { } da)
            {
                var agent = Text(da, "launch.defaultAgent") ?? "auto";
                if (agent != "auto" && !LaunchProtocol.Agents.Contains(agent)) { throw new FormatException($"launch.defaultAgent '{agent}' is not auto or one of {string.Join(", ", LaunchProtocol.Agents)}"); }
                config = config with { DefaultAgent = agent };
            }
            if (Get(l, "permissions") is { } p)
            {
                var permissions = Text(p, "launch.permissions") ?? LaunchProtocol.Ask;
                if (permissions is not (LaunchProtocol.Ask or LaunchProtocol.AllowAll)) { throw new FormatException($"launch.permissions is not '{LaunchProtocol.Ask}' or '{LaunchProtocol.AllowAll}'"); }
                config = config with { Permissions = permissions };
            }
            if (Get(l, "worktrees") is { } w)
            {
                if (w.ValueKind != JsonValueKind.Object) { throw new FormatException("launch.worktrees is not an object"); }
                if (Get(w, "root") is { } root) { config = config with { WorktreeRoot = Text(root, "launch.worktrees.root") ?? config.WorktreeRoot }; }
                if (Get(w, "limit") is { } limit) { config = config with { WorktreeLimit = (int)Number(limit, "launch.worktrees.limit", 0, 1000, whole: true) }; }
                if (Get(w, "idleHours") is { } idle) { config = config with { WorktreeIdleHours = Number(idle, "launch.worktrees.idleHours", 0, 24 * 365) }; }
            }
            if (Get(l, "profiles") is { } profiles) { config = config with { Profiles = Strings(profiles, "launch.profiles") }; }
            if (Get(l, "defaultProfile") is { } dp) { config = config with { DefaultProfile = Text(dp, "launch.defaultProfile") }; }
            if (Get(l, "resumeCount") is { } rc) { config = config with { ResumeCount = (int)Number(rc, "launch.resumeCount", 0, LaunchProtocol.MaxResumable, whole: true) }; }
            if (Get(l, "transfer") is { } tr) { config = config with { Transfer = ReadTransfer(tr) }; }
            if (Get(l, "remoteFolderTrust") is { } rft) { config = config with { RemoteFolderTrust = Bool(rft, "launch.remoteFolderTrust") }; }
            if (Get(l, "remoteFolderTrustDevices") is { } rfd)
            {
                var ids = Strings(rfd, "launch.remoteFolderTrustDevices");
                foreach (var bad in ids.Where(i => !TransferProtocol.IsDeviceId(i))) { problems.Add($"launch.remoteFolderTrustDevices: '{bad}' is not a device id; ignored"); }
                config = config with { RemoteFolderTrustDevices = [.. ids.Where(TransferProtocol.IsDeviceId)] };
            }
            if (Get(l, "agents") is { } agents)
            {
                if (agents.ValueKind != JsonValueKind.Object) { throw new FormatException("launch.agents is not an object"); }
                var map = new Dictionary<string, AgentConfig>(StringComparer.Ordinal);
                foreach (var entry in agents.EnumerateObject())
                {
                    if (!LaunchProtocol.Agents.Contains(entry.Name)) { problems.Add($"launch.agents.{entry.Name} is not an agent this host launches; ignored"); continue; }
                    map[entry.Name] = ReadAgent(entry.Value, $"launch.agents.{entry.Name}");
                }
                config = config with { Agents = map };
            }
            return (config, problems);
        }
        catch (FormatException e)
        {
            problems.Add($"{e.Message}; launching is off");
            return (Default with { Enabled = false }, problems);
        }
    }

    static List<WorkspaceConfig> ReadWorkspaces(JsonElement workspaces, List<string> problems)
    {
        if (workspaces.ValueKind != JsonValueKind.Array) { throw new FormatException("launch.workspaces is not an array"); }
        var list = new List<WorkspaceConfig>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var w in workspaces.EnumerateArray())
        {
            var at = $"launch.workspaces[{i++}]";
            if (list.Count >= LaunchProtocol.MaxWorkspaces) { problems.Add($"{at} and after: at most {LaunchProtocol.MaxWorkspaces} workspaces; ignored"); break; }
            string? path = null, label = null;
            var isolate = false;
            if (w.ValueKind == JsonValueKind.String) { path = w.GetString(); }
            else if (w.ValueKind == JsonValueKind.Object)
            {
                path = Get(w, "path") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
                label = Get(w, "label") is { ValueKind: JsonValueKind.String } lb ? lb.GetString() : null;
                if (Get(w, "isolate") is { } iso)
                {
                    // A setting the person may have meant as on is never read as off: an entry
                    // whose isolate is not a boolean is not a place to launch at all.
                    if (iso.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { problems.Add($"{at}.isolate is not true or false; the workspace is left out"); continue; }
                    isolate = iso.ValueKind == JsonValueKind.True;
                }
            }
            if (string.IsNullOrWhiteSpace(path)) { problems.Add($"{at} has no path; left out"); continue; }
            var full = Folders.Expand(path);
            if (full is null) { problems.Add($"{at}.path is not an absolute path; left out"); continue; }
            label = string.IsNullOrWhiteSpace(label) ? Folders.DefaultLabel(full) : label.Trim();
            label = Limits.KeepStart(label, LaunchProtocol.MaxLabel)!;
            // Two entries under one label could not be told apart by a client: the first keeps
            // it, as agent-ha-bridge's Get-BridgeWorkspaceChoices does.
            if (!labels.Add(label)) { label = Limits.KeepStart($"{label} ({full})", LaunchProtocol.MaxLabel)!; labels.Add(label); }
            list.Add(new WorkspaceConfig(label, full, isolate));
        }
        return list;
    }

    static TransferConfig ReadTransfer(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) { throw new FormatException("launch.transfer is not an object"); }
        var config = TransferConfig.Default;
        if (Get(t, "serve") is { } serve) { config = config with { Serve = Bool(serve, "launch.transfer.serve") }; }
        if (Get(t, "receive") is { } receive) { config = config with { Receive = Bool(receive, "launch.transfer.receive") }; }
        if (Get(t, "maxBytes") is { } max)
        {
            config = config with { MaxBytes = (long)Number(max, "launch.transfer.maxBytes", 1024 * 1024, TransferProtocol.MostBytes, whole: true) };
        }
        config = config with { MaxSourceBytes = config.MaxBytes * 10 };
        if (Get(t, "maxSourceBytes") is { } source)
        {
            config = config with { MaxSourceBytes = (long)Number(source, "launch.transfer.maxSourceBytes", config.MaxBytes, 20L * 1024 * 1024 * 1024, whole: true) };
        }
        return config;
    }

    static AgentConfig ReadAgent(JsonElement a, string at)
    {
        if (a.ValueKind != JsonValueKind.Object) { throw new FormatException($"{at} is not an object"); }
        var config = AgentConfig.Default;
        if (Get(a, "path") is { } path) { config = config with { Path = Text(path, $"{at}.path") }; }
        if (Get(a, "model") is { } model) { config = config with { Model = Text(model, $"{at}.model") }; }
        if (Get(a, "effort") is { } effort) { config = config with { Effort = Text(effort, $"{at}.effort") }; }
        if (Get(a, "context") is { } context) { config = config with { Context = Text(context, $"{at}.context") }; }
        if (Get(a, "models") is { } models) { config = config with { Models = Strings(models, $"{at}.models") }; }
        if (Get(a, "efforts") is { } efforts) { config = config with { Efforts = Strings(efforts, $"{at}.efforts") }; }
        if (Get(a, "contexts") is { } contexts) { config = config with { Contexts = Strings(contexts, $"{at}.contexts") }; }
        if (Get(a, "extraArgs") is { } extra) { config = config with { ExtraArgs = Strings(extra, $"{at}.extraArgs", unique: false) }; }
        return config;
    }

    // Absent and null are the same thing, as on the wire.
    static JsonElement? Get(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    static bool Bool(JsonElement v, string at) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException($"{at} is not true or false"),
    };

    static string? Text(JsonElement v, string at)
    {
        if (v.ValueKind != JsonValueKind.String) { throw new FormatException($"{at} is not a string"); }
        var s = v.GetString()!.Trim();
        return s.Length == 0 ? null : s;
    }

    static double Number(JsonElement v, string at, double min, double max, bool whole = false)
    {
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || double.IsNaN(d) || d < min || d > max || (whole && d != Math.Floor(d)))
        {
            throw new FormatException($"{at} is not a {(whole ? "whole number" : "number")} from {min} to {max}");
        }
        return d;
    }

    static IReadOnlyList<string> Strings(JsonElement v, string at, bool unique = true)
    {
        if (v.ValueKind != JsonValueKind.Array) { throw new FormatException($"{at} is not an array of strings"); }
        var list = new List<string>();
        foreach (var s in v.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.String) { throw new FormatException($"{at} is not an array of strings"); }
            var text = unique ? s.GetString()!.Trim() : s.GetString()!;
            if (text.Length == 0 || (unique && list.Contains(text, StringComparer.Ordinal))) { continue; }
            list.Add(text);
            if (list.Count > LaunchProtocol.MaxChoices) { throw new FormatException($"{at} has more than {LaunchProtocol.MaxChoices} entries"); }
        }
        return list;
    }
}

// launch.transfer: whether this machine hands its ended sessions to the account's other
// machines (serve) and brings theirs in (receive), and how large a bundle may be: MaxBytes on
// the wire, compressed (agent-ha-bridge's newSession.transferMaxBytes, 25 MB, at most 200 MB),
// and MaxSourceBytes read from disk to make one (newSession.transferMaxSourceBytes, ten times
// that), which also bounds what a bundle may unpack to here.
public sealed record TransferConfig
{
    public static readonly TransferConfig Default = new();

    public bool Serve { get; init; } = true;
    public bool Receive { get; init; } = true;
    public long MaxBytes { get; init; } = TransferProtocol.DefaultMaxBytes;
    public long MaxSourceBytes { get; init; } = TransferProtocol.DefaultMaxBytes * 10;
}

// One approved folder. Path is absolute, as written (with ~ expanded); Isolate starts each new
// session in a git worktree of its own.
public sealed record WorkspaceConfig(string Label, string Path, bool Isolate);

// One launcher's settings. Model, effort and context are its defaults; the lists replace the
// built-in ones (Launchers). Agency reads Copilot's tuning, since that is the command line it
// passes on. ExtraArgs go on every launch's command line, as written: the person's own.
public sealed record AgentConfig
{
    public static readonly AgentConfig Default = new();

    public string? Path { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<string> Efforts { get; init; } = [];
    public IReadOnlyList<string> Contexts { get; init; } = [];
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}
