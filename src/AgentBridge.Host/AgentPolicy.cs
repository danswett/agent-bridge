using System.Text.Json;

namespace AgentBridge.Host;

// config.json's agents section (docs/host-config.md; design/agent-principals.md 5.1): what this
// machine lets agents do, in both directions. It is changed only at the machine, so nothing here
// is ever read from a wire request. Nothing consumes it yet (#720 slice 4 and the audit log do).
//
// Fail closed: an absent section is Off (agents are off, every grant off, the design's default
// limits). A section, or a nested object, that is not what it should be is Off too, and said.
// A single bad key falls back to that key's own safe default, which is always the narrowest
// value, so an error never widens a grant. Limits can only be lowered: the design's number is
// both the default and the ceiling, and zero, a negative or anything above it is the default.
// Unknown keys are said and ignored.
public sealed record AgentPolicy
{
    public const int DefaultCredentialDays = 7;
    public const string ScopeNone = "none", ScopeRead = "read", ScopeReadReply = "read+reply", ScopeReadReplyLaunch = "read+reply+launch";

    public static readonly AgentPolicy Off = new();

    public bool Enabled { get; init; }
    public int CredentialDays { get; init; } = DefaultCredentialDays;
    public string IssueLaunched { get; init; } = ScopeNone;
    public string IssueAdopted { get; init; } = ScopeNone;
    public bool OutboundRemote { get; init; }
    public bool OutboundAllowAgentLaunchedToLaunch { get; init; }
    public AgentInboundPolicy InboundLocal { get; init; } = AgentInboundPolicy.Off;
    public AgentInboundPolicy InboundRemote { get; init; } = AgentInboundPolicy.Off;
    public IReadOnlyList<string> ExcludedSessions { get; init; } = [];
    public IReadOnlyList<string> ExcludedWorkspaces { get; init; } = [];
    public AgentPolicyLimits Limits { get; init; } = AgentPolicyLimits.Default;

    // "prefix" or "none"; the design forces the prefix for cross-account, so prefix is the default.
    public string Label { get; init; } = "prefix";

    public static (AgentPolicy Policy, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        var problems = new List<string>();
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Off, problems); }
        if (s.ValueKind != JsonValueKind.Object) { return Denied(problems, "agents is not an object"); }
        var policy = Off;
        var inboundLocal = AgentInboundPolicy.Off;
        var inboundRemote = AgentInboundPolicy.Off;
        foreach (var p in s.EnumerateObject())
        {
            switch (p.Name)
            {
                case "enabled": policy = policy with { Enabled = Bool(p.Value, "agents.enabled", problems) }; break;
                case "credentialDays": policy = policy with { CredentialDays = Int(p.Value, "agents.credentialDays", 1, DefaultCredentialDays, DefaultCredentialDays, problems) }; break;
                case "label":
                    policy = policy with { Label = Choice(p.Value, "agents.label", ["prefix", "none"], "prefix", problems) };
                    break;
                case "links":
                    if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 0) { problems.Add("agents.links must be an empty list in this version; ignored"); }
                    break;
                case "issue":
                    if (!Object(p.Value, "agents.issue", problems, out var issue)) { return Denied(problems); }
                    var scopes = new[] { ScopeNone, ScopeRead, ScopeReadReply, ScopeReadReplyLaunch };
                    foreach (var i in issue.EnumerateObject())
                    {
                        if (i.Name == "launched") { policy = policy with { IssueLaunched = Choice(i.Value, "agents.issue.launched", scopes, ScopeNone, problems) }; }
                        else if (i.Name == "adopted") { policy = policy with { IssueAdopted = Choice(i.Value, "agents.issue.adopted", scopes, ScopeNone, problems) }; }
                        else { Unknown($"agents.issue.{i.Name}", problems); }
                    }
                    break;
                case "outbound":
                    if (!Object(p.Value, "agents.outbound", problems, out var outbound)) { return Denied(problems); }
                    foreach (var o in outbound.EnumerateObject())
                    {
                        if (o.Name == "remote") { policy = policy with { OutboundRemote = Bool(o.Value, "agents.outbound.remote", problems) }; }
                        else if (o.Name == "allowAgentLaunchedToLaunch") { policy = policy with { OutboundAllowAgentLaunchedToLaunch = Bool(o.Value, "agents.outbound.allowAgentLaunchedToLaunch", problems) }; }
                        else { Unknown($"agents.outbound.{o.Name}", problems); }
                    }
                    break;
                case "inbound":
                    if (!Object(p.Value, "agents.inbound", problems, out var inbound)) { return Denied(problems); }
                    foreach (var i in inbound.EnumerateObject())
                    {
                        if (i.Name is "local" or "remote")
                        {
                            if (!Object(i.Value, $"agents.inbound.{i.Name}", problems, out var side)) { return Denied(problems); }
                            var parsed = ReadInbound(side, $"agents.inbound.{i.Name}", i.Name == "remote", problems);
                            if (i.Name == "local") { inboundLocal = parsed; } else { inboundRemote = parsed; }
                        }
                        else if (i.Name == "exclude")
                        {
                            if (!Object(i.Value, "agents.inbound.exclude", problems, out var exclude)) { return Denied(problems); }
                            foreach (var e in exclude.EnumerateObject())
                            {
                                if (e.Name == "sessions") { policy = policy with { ExcludedSessions = Strings(e.Value, "agents.inbound.exclude.sessions", problems) ?? [] }; }
                                else if (e.Name == "workspaces") { policy = policy with { ExcludedWorkspaces = Strings(e.Value, "agents.inbound.exclude.workspaces", problems) ?? [] }; }
                                else { Unknown($"agents.inbound.exclude.{e.Name}", problems); }
                            }
                        }
                        else { Unknown($"agents.inbound.{i.Name}", problems); }
                    }
                    break;
                case "limits":
                    if (!Object(p.Value, "agents.limits", problems, out var limits)) { return Denied(problems); }
                    policy = policy with { Limits = AgentPolicyLimits.Read(limits, problems) };
                    break;
                default: Unknown($"agents.{p.Name}", problems); break;
            }
        }
        return (policy with { InboundLocal = inboundLocal, InboundRemote = inboundRemote }, problems);
    }

    static AgentInboundPolicy ReadInbound(JsonElement side, string path, bool remote, List<string> problems)
    {
        var result = AgentInboundPolicy.Off;
        foreach (var p in side.EnumerateObject())
        {
            var key = $"{path}.{p.Name}";
            switch (p.Name)
            {
                case "fromMachines" when remote:
                    // "any" is a wide grant, so it is only accepted spelled out; the narrowest reading is none.
                    result = result with { FromMachines = p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "any" ? ["any"] : Strings(p.Value, key, problems) ?? [] };
                    break;
                case "read": result = result with { Read = Choice(p.Value, key, ["any", "agent-launched", "none"], "none", problems) }; break;
                case "reply": result = result with { Reply = Choice(p.Value, key, ["forced-ask", "agent-launched", "none"], "none", problems) }; break;
                case "gate": result = result with { Gate = Choice(p.Value, key, ["off", "quiet", "push"], "off", problems) }; break;
                case "allowAllTargets": result = result with { AllowAllTargets = AllowAll(p.Value, key, problems) }; break;
                case "coordinationGroups": result = result with { CoordinationGroups = Groups(p.Value, key, problems) }; break;
                case "launch": result = result with { Launch = Launch(p.Value, key, problems) }; break;
                default: Unknown(key, problems); break;
            }
        }
        return result;
    }

    // false (the default) or { "workspaces": "same" | "any" }.
    static string? AllowAll(JsonElement v, string key, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.False || v.ValueKind == JsonValueKind.Null) { return null; }
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("workspaces", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() is "same" or "any") { return w.GetString(); }
        problems.Add($"{key} is not false or {{ \"workspaces\": \"same\" or \"any\" }}; it is false");
        return null;
    }

    // false (the default) or { maxHours 4..24, perMemberPerDay, perGroupPerMinute }; all three are required.
    static AgentCoordinationGroups? Groups(JsonElement v, string key, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.False || v.ValueKind == JsonValueKind.Null) { return null; }
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("maxHours", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var hours) && hours is >= 4 and <= 24
            && v.TryGetProperty("perMemberPerDay", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var perDay) && perDay >= 1
            && v.TryGetProperty("perGroupPerMinute", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var perMinute) && perMinute >= 1
            && v.EnumerateObject().Count() == 3)
        {
            return new AgentCoordinationGroups(hours, perDay, perMinute);
        }
        problems.Add($"{key} is not false or {{ maxHours 4 to 24, perMemberPerDay, perGroupPerMinute }} with whole numbers from 1; it is false");
        return null;
    }

    // false (the default) or { workspaces: "approved" | [labels], permissions: "ask", agents: [ids], profiles: [names] }.
    static AgentLaunchGrant? Launch(JsonElement v, string key, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.False || v.ValueKind == JsonValueKind.Null) { return null; }
        if (v.ValueKind != JsonValueKind.Object) { problems.Add($"{key} is not false or an object; it is false"); return null; }
        IReadOnlyList<string> workspaces = [];
        IReadOnlyList<string> agents = [];
        IReadOnlyList<string> profiles = [];
        var ok = true;
        foreach (var p in v.EnumerateObject())
        {
            var k = $"{key}.{p.Name}";
            switch (p.Name)
            {
                case "workspaces":
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "approved") { workspaces = ["approved"]; }
                    else if (Strings(p.Value, k, problems) is { } labels) { workspaces = labels; }
                    else { ok = false; }
                    break;
                // An unattended launch is ask only; allowAll is a gated launch, never a standing grant.
                case "permissions":
                    if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() != "ask") { problems.Add($"{k} is not \"ask\"; the launch grant is off"); ok = false; }
                    break;
                case "agents": if (Strings(p.Value, k, problems) is { } a) { agents = a; } else { ok = false; } break;
                case "profiles": if (Strings(p.Value, k, problems) is { } pr) { profiles = pr; } else { ok = false; } break;
                default: Unknown(k, problems); break;
            }
        }
        if (!v.TryGetProperty("permissions", out _)) { problems.Add($"{key}.permissions must be stated as \"ask\"; the launch grant is off"); ok = false; }
        return ok && workspaces.Count > 0 ? new AgentLaunchGrant(workspaces, agents, profiles) : null;
    }

    static (AgentPolicy, IReadOnlyList<string>) Denied(List<string> problems, string? why = null)
    {
        if (why is not null) { problems.Add(why); }
        problems.Add("agents: every agent command, inbound and outbound, is denied until it is fixed");
        return (Off, problems);
    }

    static bool Object(JsonElement v, string path, List<string> problems, out JsonElement obj)
    {
        obj = v;
        if (v.ValueKind == JsonValueKind.Object) { return true; }
        problems.Add($"{path} is not an object");
        return false;
    }

    static void Unknown(string path, List<string> problems) => problems.Add($"{path} is not a setting; ignored");

    static bool Bool(JsonElement v, string path, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.True) { return true; }
        if (v.ValueKind != JsonValueKind.False && v.ValueKind != JsonValueKind.Null) { problems.Add($"{path} is not true or false, so it is false"); }
        return false;
    }

    static string Choice(JsonElement v, string path, string[] allowed, string fallback, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s && allowed.Contains(s)) { return s; }
        if (v.ValueKind != JsonValueKind.Null) { problems.Add($"{path} is not one of {string.Join(", ", allowed)}, so it is {fallback}"); }
        return fallback;
    }

    internal static int Int(JsonElement v, string path, int min, int max, int fallback, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= min && n <= max) { return n; }
        if (v.ValueKind != JsonValueKind.Null) { problems.Add($"{path} is not a whole number from {min} to {max}, so it is {fallback}"); }
        return fallback;
    }

    static List<string>? Strings(JsonElement v, string path, List<string> problems)
    {
        if (v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())))
        {
            return v.EnumerateArray().Select(e => e.GetString()!).Distinct().Take(64).ToList();
        }
        problems.Add($"{path} is not a list of names, so it is empty");
        return null;
    }
}

public sealed record AgentInboundPolicy
{
    public static readonly AgentInboundPolicy Off = new();

    // Only inbound.remote has it: "any" or the machines named; empty reaches none.
    public IReadOnlyList<string> FromMachines { get; init; } = [];
    public string Read { get; init; } = "none";
    public string Reply { get; init; } = "none";
    public string Gate { get; init; } = "off";
    // null is off; otherwise "same" or "any".
    public string? AllowAllTargets { get; init; }
    public AgentCoordinationGroups? CoordinationGroups { get; init; }
    public AgentLaunchGrant? Launch { get; init; }
}

public sealed record AgentCoordinationGroups(int MaxHours, int PerMemberPerDay, int PerGroupPerMinute);

public sealed record AgentLaunchGrant(IReadOnlyList<string> Workspaces, IReadOnlyList<string> Agents, IReadOnlyList<string> Profiles);

public sealed record AgentPolicyLimits
{
    public static readonly AgentPolicyLimits Default = new();

    public int MaxDepth { get; init; } = 3;
    public int ConversationMessages { get; init; } = 20;
    public int ConversationMinutes { get; init; } = 60;
    public int PerPairPerMinute { get; init; } = 6;
    public int PerTargetPerMinute { get; init; } = 12;
    public int LaunchesPerOriginPerHour { get; init; } = 3;
    public int LaunchesPerMachinePerHour { get; init; } = 10;

    internal static AgentPolicyLimits Read(JsonElement limits, List<string> problems)
    {
        var d = Default;
        var result = d;
        foreach (var p in limits.EnumerateObject())
        {
            var path = $"agents.limits.{p.Name}";
            switch (p.Name)
            {
                case "maxDepth": result = result with { MaxDepth = AgentPolicy.Int(p.Value, path, 1, d.MaxDepth, d.MaxDepth, problems) }; break;
                case "conversationMessages": result = result with { ConversationMessages = AgentPolicy.Int(p.Value, path, 1, d.ConversationMessages, d.ConversationMessages, problems) }; break;
                case "conversationMinutes": result = result with { ConversationMinutes = AgentPolicy.Int(p.Value, path, 1, d.ConversationMinutes, d.ConversationMinutes, problems) }; break;
                case "perPairPerMinute": result = result with { PerPairPerMinute = AgentPolicy.Int(p.Value, path, 1, d.PerPairPerMinute, d.PerPairPerMinute, problems) }; break;
                case "perTargetPerMinute": result = result with { PerTargetPerMinute = AgentPolicy.Int(p.Value, path, 1, d.PerTargetPerMinute, d.PerTargetPerMinute, problems) }; break;
                case "launchesPerOriginPerHour": result = result with { LaunchesPerOriginPerHour = AgentPolicy.Int(p.Value, path, 1, d.LaunchesPerOriginPerHour, d.LaunchesPerOriginPerHour, problems) }; break;
                case "launchesPerMachinePerHour": result = result with { LaunchesPerMachinePerHour = AgentPolicy.Int(p.Value, path, 1, d.LaunchesPerMachinePerHour, d.LaunchesPerMachinePerHour, problems) }; break;
                default: problems.Add($"{path} is not a setting; ignored"); break;
            }
        }
        return result;
    }
}
