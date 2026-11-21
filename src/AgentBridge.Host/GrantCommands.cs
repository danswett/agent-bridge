using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class GrantCommands
{
    public const string Usage = "grants issue <document.json> | grants revoke <id> <generation> | grants list [--profile <name>]";

    public static async Task<int> RunAsync(string[] args, string root, TextWriter output, TextWriter error)
    {
        var profile = "default";
        if (args is [.. var command, "--profile", var selected]) { args = command; profile = selected; }
        if (!Identity.IsValidProfile(profile)) { error.WriteLine("invalid profile"); return 2; }
        var folder = Path.Combine(root, profile);
        var audit = new AgentAuditLog(folder);
        bool Refused()
        {
            var ancestry = ProcessChain.Read(Environment.ProcessId);
            var known = new Func<IReadOnlyList<KnownProcess>>(() => KnownAgents.Running(new SystemProcessInspector()));
            return Console.IsInputRedirected || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_BRIDGE_TOKEN"))
                || AgentAncestry.Inside(Environment.GetEnvironmentVariable(AgentAncestry.SessionVariable), ancestry, known) is not null
                || new CallerGuard(known, null, hostImage: Environment.ProcessPath).Classify(ancestry).Kind != CallerKind.HostCli;
        }
        if (args is not ["grants", "list"] && Refused()) { return Deny(args, audit, error); }
        try
        {
            // Never provision a device key just to inspect or issue a grant.
            if (!OperatingSystem.IsWindows() || !DeviceKeys.Exists(folder))
            { error.WriteLine("refused: an existing Windows host identity is required"); return 4; }
            var identity = Identity.Load(profile, root: root);
            using var key = identity.Key;
            HostPolicySnapshot? policy = null;
            HostSettings? acceptedSettings = null;
            string? document = null;
            if (args is ["grants", "issue", _])
            {
                document = ReadDocument(args[2]);
                var candidate = JsonSerializer.Deserialize<GrantDocument>(document, ProtocolJson.Options)
                    ?? throw new JsonException("grant document required");
                var shape = GrantValidator.Parse(document, candidate.Scope);
                if (!shape.Valid) { error.WriteLine($"refused: invalid grant ({shape.Reason})"); return 2; }
                var (settings, problem) = HostSettings.Load(folder);
                acceptedSettings = settings;
                if (problem is not null || settings.SectionProblems.Any() || settings.ReadOnly
                    || !settings.Launch.Enabled || !settings.Agents.Enabled || !settings.Agents.LaunchEnabled
                    || settings.Agents.InboundLocal.Launch is not { } inbound || identity.Account is null)
                { error.WriteLine("refused: receiving host launch policy is unavailable or off"); return 4; }
                await using var local = await LocalClient.ConnectAsync(LocalEndpoint.For(folder));
                await local.HelloAsync();
                var offered = await local.ReadAsync<LaunchOptions>(LaunchProtocol.Options, new { });
                var agents = offered.Agents.Where(a => candidate.Scope.Agents.Contains(a.Id)
                    && inbound.Agents.Contains(a.Id, StringComparer.Ordinal)).ToArray();
                var device = Wire.DeviceId(key.Public);
                var scope = new GrantScope([device],
                    [.. offered.Workspaces.Where(w => w.Isolate == true && !settings.Agents.ExcludedWorkspaces.Contains(w.Label)
                        && (inbound.Workspaces.Contains("approved") || inbound.Workspaces.Contains(w.Label))).Select(w => w.Label)],
                    [.. agents.Select(a => a.Id)], Common(agents.Select(a => a.Models)),
                    Common(agents.Select(a => a.Id == "agency" ? (IReadOnlyList<string>)[.. (a.Profiles ?? []).Where(p => inbound.Profiles.Contains(p))] : ["default"])),
                    Common(agents.Select(a => a.Efforts)), Common(agents.Select(a => a.Contexts)),
                    inbound.AllowAllLaunch ? ["ask", "allowAll"] : ["ask"]);
                var limits = settings.Agents.Limits.Launch;
                if (candidate.Subjects.Any(s => settings.Agents.ExcludedSessions.Contains(s.Session)))
                { error.WriteLine("refused: excluded subject"); return 4; }
                policy = new(identity.Account.AccountId, new(device, profile), candidate.Subjects[0], scope,
                    new(Math.Min(limits.LaunchesPerOriginPerHour, limits.LaunchesPerMachinePerHour), 5000, limits.LiveAgentLaunched),
                    true, true, inbound.AllowAllLaunch);
            }
            return Run(args, Refused, audit, () => new(folder, key, audit, policy?.Scope!),
                () =>
                {
                    var (latest, problem) = HostSettings.Load(folder);
                    return policy is null ? throw new InvalidOperationException("policy unavailable")
                        : policy with { LaunchAllowed = problem is null && JsonSerializer.Serialize(latest) == JsonSerializer.Serialize(acceptedSettings) };
                }, _ => document ?? throw new JsonException("document unavailable"),
                digest =>
                {
                    output.WriteLine($"Confirm this exact grant digest: {digest}");
                    output.Flush();
                    return Console.IsInputRedirected ? null : Console.ReadLine();
                }, key, output, error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException
            or HostErrorException or TimeoutException or InvalidOperationException)
        { error.WriteLine($"refused: grant operation unavailable ({e.GetType().Name})"); return 4; }
    }

    static ImmutableArray<string> Common(IEnumerable<IReadOnlyList<string>> lists)
    {
        var rows = lists.ToArray();
        return rows.Length == 0 ? [] : rows.Skip(1).Aggregate(rows[0].AsEnumerable(),
            (all, next) => all.Intersect(next, StringComparer.Ordinal)).ToImmutableArray();
    }
    static string ReadDocument(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024) { throw new IOException("grant document is too large"); }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static int Run(string[] args, Func<bool> refused, AgentAuditLog audit, Func<GrantStore> open,
        Func<HostPolicySnapshot> policy, Func<string, string> read, Func<string, string?> confirm,
        P256KeyPair key, TextWriter output, TextWriter error, TimeProvider? time = null)
    {
        int Fail(string message, int code = 4) { error.WriteLine($"refused: {message}"); return code; }
        if (args is not ["grants", "list"] && refused()) { return Deny(args, audit, error); }
        if (args is ["grants", "list"])
        {
            var listed = open().List();
            if (listed.Problem != GrantStoreProblem.None) { return Fail(listed.Problem.ToString()); }
            output.WriteLine(JsonSerializer.Serialize(listed.Grants.Select(g => new
            {
                g.Id, g.Generation, g.Issuer.Account, g.Issuer.Device, g.Issuer.Digest, g.Subjects, g.Actions,
                g.Scope, g.Caps, g.ExpiresAt, g.AllowAllLaunch
            }), ProtocolJson.Options));
            return 0;
        }
        if (args is ["grants", "revoke", var id, var revision] && Guid.TryParseExact(id, "D", out var uuid)
            && uuid != Guid.Empty && long.TryParse(revision, out var generation) && generation > 0)
        {
            if (refused()) { return Deny(args, audit, error); }
            var revoked = open().Revoke(uuid, generation);
            if (!revoked.Usable) { return Fail(revoked.Problem.ToString()); }
            output.WriteLine(JsonSerializer.Serialize(new { id = uuid, generation, tombstone = true }, ProtocolJson.Options));
            return 0;
        }
        if (args is not ["grants", "issue", var file]) { return Fail(Usage, 2); }
        var current = policy();
        var parsed = GrantValidator.Parse(read(file), current.Scope);
        if (!parsed.Valid || parsed.Grant is not { ExpiresAt: not null } grant)
        { return Fail($"explicit valid subjects, scope, caps and expiry required ({parsed.Reason})", 2); }
        var now = (time ?? TimeProvider.System).GetUtcNow();
        grant = grant with { Issuer = new(current.Account, current.Audience.Device, "AQ", grant.Issuer.IssuedAt, new('0', 64)) };
        var valid = GrantValidator.Validate(grant, current.Scope);
        if (!valid.Valid || !Within(grant, current, now) || grant.Scope.Machines.Length != 1
            || grant.StopTargets is { IsDefault: false, Length: > 0 })
        { return Fail("grant exceeds receiving host policy"); }
        grant = valid.Grant! with { Roles = grant.Roles.IsDefault ? [] : grant.Roles,
            StopTargets = grant.StopTargets.IsDefault ? [] : grant.StopTargets };
        // Digest covers the complete normalized document except the proof and digest themselves.
        var body = JsonSerializer.SerializeToUtf8Bytes(grant with
        { Issuer = grant.Issuer with { PersonProof = "", Digest = "" } }, ProtocolJson.Options);
        var digest = Convert.ToHexStringLower(SHA256.HashData(body));
        output.WriteLine(JsonSerializer.Serialize(new
        { grant.Id, grant.Generation, grant.Subjects, grant.Actions, grant.Scope, grant.Caps, grant.ExpiresAt, grant.AllowAllLaunch }, ProtocolJson.Options));
        if (confirm(digest) != digest) { return Fail("exact person digest was not confirmed"); }
        if (refused()) { return Deny(args, audit, error); }
        var latest = policy();
        if (!GrantValidator.Validate(grant, latest.Scope).Valid || !Within(grant, latest, (time ?? TimeProvider.System).GetUtcNow()))
        { return Fail("receiving host policy changed or grant expired before issuance"); }
        grant = grant with { Issuer = grant.Issuer with
        { Digest = digest, PersonProof = AgentBridge.Roster.Codec.Base64Url(CommandMac.Of(key, key.Public, body)) } };
        var issued = open().Issue(grant);
        if (!issued.Usable) { return Fail(issued.Problem.ToString()); }
        output.WriteLine(JsonSerializer.Serialize(new
        { grant.Id, grant.Generation, digest, grant.AllowAllLaunch, state = "issued" }, ProtocolJson.Options));
        return 0;
    }

    static bool Within(GrantDocument grant, HostPolicySnapshot policy, DateTimeOffset now) =>
        grant.Account == policy.Account && grant.Audience == policy.Audience && policy.LaunchAllowed
        && (!grant.Actions.Contains("stop") || policy.StopAllowed) && grant.Issuer.IssuedAt <= now && grant.ExpiresAt > now
        && (!grant.Scope.Permissions.Contains("allowAll") || grant.AllowAllLaunch && policy.AllowAll)
        && grant.Caps.Hourly <= policy.Caps.Hourly && grant.Caps.Daily <= policy.Caps.Daily && grant.Caps.Live <= policy.Caps.Live;

    static int Deny(string[] args, AgentAuditLog audit, TextWriter error)
    {
        var recorded = audit.Append(new("local", Guid.NewGuid().ToString(), "nonperson:local", [],
            args.ElementAtOrDefault(1) == "revoke" ? "grants.revoke.refused" : "grants.issue.refused", "grants", "refused"));
        error.WriteLine(recorded ? "refused: person invocation required" : "refused: person invocation required; audit unavailable");
        return 4;
    }
}
