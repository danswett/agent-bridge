using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class AgentCommands
{
    public const string Usage = "agent-bridge-host agents adopt <session> [--scope read|read+reply] | agents revoke <session> | agents credential | agents lite kill"
        + " | agents launch --agent <id> --workspace <label> [--request <uuid>] [--model|--effort|--context|--role|--prompt|--fleet-name <value>]"
        + " | agents launch-status --request <uuid> [--profile <name>]";

    static readonly string[] LaunchFlags = ["--request", "--agent", "--workspace", "--model", "--effort", "--context", "--role", "--prompt", "--fleet-name"];

    static Dictionary<string, string>? Flags(IReadOnlyList<string> args, string[] allowed)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count || !allowed.Contains(args[i]) || !flags.TryAdd(args[i], args[i + 1])) { return null; }
        }
        return flags.TryGetValue("--request", out var id) && !Guid.TryParseExact(id, "D", out _) ? null : flags;
    }

    public static Task<int> RunWithProfileAsync(IReadOnlyList<string> args, string root, Func<bool> refused, TextWriter output, TextWriter error)
    {
        var rest = args.ToList();
        var p = rest.IndexOf("--profile");
        var profile = p < 0 ? "default" : rest.ElementAtOrDefault(p + 1);
        if (profile is null || !Identity.IsValidProfile(profile) || (p >= 0 && rest.LastIndexOf("--profile") != p))
        { error.WriteLine("agent-bridge-host: --profile requires one valid profile name"); return Task.FromResult(2); }
        if (p >= 0) { rest.RemoveRange(p, 2); }
        return RunAsync(rest, Path.Combine(root, profile), refused, output, error);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, string folder, Func<bool> refused, TextWriter output, TextWriter error)
    {
        var verb = args.ElementAtOrDefault(1);
        var target = args.ElementAtOrDefault(2);
        var flags = verb is "launch" or "launch-status" ? Flags(args, verb == "launch" ? LaunchFlags : ["--request"]) : null;
        var valid = args switch
        {
            ["agents", "launch", ..] => flags is { } f && f.ContainsKey("--agent") && f.ContainsKey("--workspace"),
            ["agents", "launch-status", ..] => flags?.ContainsKey("--request") == true,
            ["agents", "credential"] => true,
            ["agents", "lite", "kill"] => true,
            ["agents", "revoke" or "adopt", var session] => !string.IsNullOrWhiteSpace(session) && !session.StartsWith("--", StringComparison.Ordinal),
            ["agents", "adopt", var session, "--scope", var scope] => !string.IsNullOrWhiteSpace(session)
                && !session.StartsWith("--", StringComparison.Ordinal) && AgentProtocol.AdoptableScopes.Contains(scope, StringComparer.Ordinal),
            _ => false,
        };
        if (!valid) { error.WriteLine("usage: " + Usage); return 2; }
        if (verb is "launch" or "launch-status") { return await LaunchAsync(verb, flags!, folder, output, error); }
        if (verb != "credential" && refused()) { return 4; }
        if (verb == "lite")
        {
            // L6: no pipe needed. The running host reads the epoch file afresh, stops its sends and tells the other hosts.
            var epoch = new Lite.LiteKill(folder, error.WriteLine).Kill();
            // The running host stays off only through the setting, so a failed write is a failed kill here.
            if (HostSettings.CurrentAgentMessaging(folder).RemoteEnabled) { error.WriteLine($"agent-bridge-host: kill epoch {epoch} saved, but agentMessaging.remote is still on; set it off"); return 1; }
            output.WriteLine($"own-agents messaging killed on this machine (epoch {epoch}); turning it on again takes config set agentMessaging.remote own-account here.");
            return 0;
        }
        if (!OperatingSystem.IsWindows()) { error.WriteLine("agent-bridge-host: adopting a session is Windows only for now"); return 2; }
        try
        {
            await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(folder));
            await channel.HelloAsync();
            if (verb == "credential")
            {
                var issued = await channel.CallAsync<AgentCredentialResult>(AgentProtocol.Credential);
                output.WriteLine(issued.Token);
                error.WriteLine($"credential for {issued.Principal}, scope {issued.Scope}, until {issued.ExpiresAt.ToLocalTime():g}. Keep it to this session.");
            }
            else if (verb == "adopt")
            {
                var adopted = await channel.CallAsync<AgentAdoptResult>(AgentProtocol.Adopt,
                    new AgentAdoptRequest { Session = target!, Scope = args.Count == 5 ? args[4] : null });
                output.WriteLine($"adopted session {adopted.Session} as {adopted.Scope}; run agent-bridge-host agents credential --profile {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))} inside it within {AgentProtocol.AdoptionMinutes} minutes.");
            }
            else
            {
                var revoked = await channel.CallAsync<AgentRevokeResult>(AgentProtocol.Revoke, new AgentRevokeRequest { Session = target! });
                output.WriteLine(revoked.Revoked ? $"session {revoked.Session} no longer acts as an agent" : $"session {revoked.Session} had no adoption or credential");
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        {
            error.WriteLine($"agent-bridge-host: {e.Message}");
            return 1;
        }
    }

    // Run inside an agent session by design (#3237 S3): the host refuses it unless that session's own
    // credential (AGENT_BRIDGE_TOKEN) has read+reply+launch scope.
    static async Task<int> LaunchAsync(string verb, Dictionary<string, string> flags, string folder, TextWriter output, TextWriter error)
    {
        if (Environment.GetEnvironmentVariable(Launchers.TokenVariable) is not { Length: > 0 } token)
        { error.WriteLine($"agent-bridge-host: agents {verb} needs this session's agent credential in {Launchers.TokenVariable}"); return 2; }
        string? Flag(string name) => flags.GetValueOrDefault(name);
        var request = Flag("--request") ?? Guid.NewGuid().ToString("D");
        if (verb == "launch") { output.WriteLine(request); }
        try
        {
            await using var channel = await LocalClient.ConnectAsync(LocalEndpoint.For(folder));
            await channel.HelloAsync();
            await channel.CallAsync(AgentProtocol.Hello, new AgentHelloRequest { Token = token });
            if (verb == "launch-status")
            {
                var s = await channel.CallAsync<AgentLaunchStatus>(AgentPipeLaunch.StatusOp, new { request });
                output.WriteLine($"{s.State}{(s.Session is null ? "" : $" session {s.Session}")}{(s.Code is null ? "" : $" ({s.Code})")}"
                    + $"; queued {s.QueuedAt.ToLocalTime():T}, changed {s.ChangedAt.ToLocalTime():T}{(s.KeptUntil is { } k ? $", kept until {k.ToLocalTime():T}" : "")}");
                return 0;
            }
            var r = await channel.CallAsync<LaunchResult>(LaunchProtocol.Launch, new LaunchRequest
            {
                Request = request, Agent = Flag("--agent"), Workspace = Flag("--workspace"), FleetName = Flag("--fleet-name"), Model = Flag("--model"),
                Effort = Flag("--effort"), Context = Flag("--context"), Role = Flag("--role"), Prompt = Flag("--prompt"), Permissions = LaunchProtocol.Ask,
            }, TimeSpan.FromMinutes(5));
            output.WriteLine($"{r.Outcome.ToString().ToLowerInvariant()}{(r.Session is null ? "" : $" session {r.Session}")}{(r.Reason is null ? "" : $": {r.Reason}")}");
            return r.Outcome == LaunchOutcome.Started ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        {
            error.WriteLine($"agent-bridge-host: {e.Message}");
            return 1;
        }
    }
}
