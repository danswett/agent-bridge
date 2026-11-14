using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What a credential is bound to (design/agent-principals.md §3.3). Id names the grant in logs and
// audit and is never the secret.
public sealed record AgentCredential(string Id, string Session, string LaunchId, int RootPid, DateTimeOffset RootStartUtc,
    string Scope, int Depth, string[] Chain, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    string Kind = "session", string? Principal = null)
{
    // Record construction only; minting and persisting a script token are separate operations.
    public static AgentCredential Script(string id, string machine, string name, DateTimeOffset issuedAt, TimeSpan? lifetime = null)
    {
        var credential = new AgentCredential(id, "", "", 0, default, AgentProtocol.ScopeReadReply, 0, [], issuedAt,
            issuedAt + (lifetime ?? TimeSpan.FromDays(AgentCredentials.DefaultScriptDays)), "script", $"script:{machine}/{name}");
        if (string.IsNullOrEmpty(id) || !AgentCredentials.ValidScript(credential, machine)) { throw new ArgumentException("invalid script credential record"); }
        return credential;
    }
}

public sealed record AgentHelloDecision(AgentCredential? Accepted, string? Refusal)
{
    public static AgentHelloDecision Refused(string why) => new(null, why);
}

// An adoption waiting for its session to ask (§3.5). No secret: the token is minted when it is claimed.
public sealed record AgentAdoption(string Session, int RootPid, DateTimeOffset RootStartUtc, string Scope, DateTimeOffset ExpiresAt);

public sealed record AgentClaimDecision(string? Token, AgentCredential? Credential, string? Refusal)
{
    public static AgentClaimDecision Refused(string why) => new(null, null, why);
}

// The credentials the host has issued to agent sessions on this machine, and who may present one
// (§3.3, §3.4). Only SHA-256(token) is stored, so the file holds nothing that authenticates, and
// a host restart doesn't strip a running session of its credential. The store fails closed: a file
// that exists and can't be read is not an empty store (unreadable is not absence), so nothing is
// accepted or issued until the owner deletes it.
//
// A token is accepted only from a process inside the session it was issued to: the credential's
// bound process (id and start time) must be among the caller's ancestors, with the ancestry read
// in full. AGENT_BRIDGE_SESSION is never consulted: any process of the user can set it. This is a
// safety rail, not a boundary: a same-user process that spoofs its parent or joins the session's
// Job object gets through (§3.4, "The residuals"). What it stops is a token pasted into another
// session, or used by a helper that didn't try.
public sealed class AgentCredentials : AgentBridge.Host.Delivery.ILaunchCredentials
{
    public const string FileName = "agent-credentials.json";
    public const int MaxCredentials = 1024;
    public const int DefaultDays = 7;
    public const int DefaultScriptDays = 30;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
        WriteIndented = true,
    };

    // A start time read from two sources of the same process can differ by a rounding.
    static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(1);

    readonly object gate = new();
    readonly string path;
    readonly string machine;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly List<Record> records = [];
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<IProtocolChannel, Connection> connections = new();
    string? problem;

    sealed record Record(string Hash, AgentCredential Credential);
    sealed record Saved(int Version, Record[] Records);
    readonly Func<int, DateTimeOffset, bool> rootAlive;

    sealed record Connection(string CredentialId, string Principal, bool Script);

    public AgentCredentials(string folder, string machine, TimeProvider? time = null, Action<string>? log = null, Func<int, DateTimeOffset, bool>? rootAlive = null)
    {
        LaunchGate = new AgentLaunchGate(folder);
        this.rootAlive = rootAlive ?? ((_, _) => true);
        path = Path.Combine(folder, FileName);
        this.machine = machine;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        Load();
    }

    public string? Problem { get { lock (gate) { return problem; } } }

    internal AgentLaunchGate LaunchGate { get; }

    public int Count { get { lock (gate) { return records.Count; } } }

    internal bool IsCredentialed(IProtocolChannel channel) => connections.TryGetValue(channel, out _);

    // The launch id an adopted session's credential carries: it was not launched by the host.
    public const string AdoptedLaunch = "adopted";
    public const int MaxPending = 64;

    readonly List<AgentAdoption> pending = [];

    // A person's yes for one session (§3.5). Nothing is issued yet: the session has to ask
    // (Claim), from inside itself, so the token goes to it and to nothing else. Held in memory:
    // a host restart lapses it, which is the safe way to lose one.
    public AgentAdoption Adopt(string session, int rootPid, DateTimeOffset rootStartUtc, string scope)
    {
        if (!AgentProtocol.AdoptableScopes.Contains(scope, StringComparer.Ordinal)) { throw new ArgumentException($"a session can be adopted as {string.Join(" or ", AgentProtocol.AdoptableScopes)}, not '{scope}'", nameof(scope)); }
        if (!ValidSession(session)) { throw new ArgumentException("an adoption is for a session id", nameof(session)); }
        var now = time.GetUtcNow();
        var adoption = new AgentAdoption(session, rootPid, rootStartUtc, scope, now + TimeSpan.FromMinutes(AgentProtocol.AdoptionMinutes));
        lock (gate)
        {
            if (problem is not null) { throw new InvalidOperationException($"agent credentials are unavailable: {problem}"); }
            if (records.Any(r => r.Credential.Session == session && r.Credential.LaunchId != AdoptedLaunch))
            {
                throw new InvalidOperationException("this session was launched with its own credential; adoption is only for sessions the host did not launch");
            }
            pending.RemoveAll(p => p.ExpiresAt <= now || p.Session == session);
            if (pending.Count >= MaxPending) { throw new InvalidOperationException("too many adoptions waiting"); }
            // A new yes replaces the old one now, not when the session next asks: the earlier
            // credential (maybe a wider scope) and its open connections end here.
            Remove(r => r.Credential.Session == session, "adopted again");
            pending.Add(adoption);
        }
        log($"agent credentials: session {session} adopted by a person at this machine as {scope}; waiting for it to ask (until {adoption.ExpiresAt:O})");
        return adoption;
    }

    // agent.credential: issues the credential an adoption waits to give, once, to a caller whose
    // ancestry was read in full and includes the adopted session's process (id and start time).
    // AGENT_BRIDGE_SESSION never counts. Exactly one adoption must cover the caller.
    public AgentClaimDecision Claim(ProcessAncestry ancestry)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (problem is not null) { return AgentClaimDecision.Refused($"the credential store is unavailable: {problem}"); }
            pending.RemoveAll(p => p.ExpiresAt <= now);
            if (!ancestry.Complete) { return Declined("the caller's ancestry could only be read in part"); }
            var covering = pending.Where(p => ancestry.Processes.Any(a => a.Id == p.RootPid && a.StartedAt is { } s && (s - p.RootStartUtc).Duration() <= StartSlack)).ToList();
            if (covering.Count == 0) { return Declined("no adopted session covers this caller"); }
            if (covering.Count > 1) { return Declined("more than one adopted session covers this caller"); }
            var adoption = covering[0];
            if (!rootAlive(adoption.RootPid, adoption.RootStartUtc))
            {
                pending.Remove(adoption);
                return Declined("the adopted session's process has ended");
            }
            string token;
            try { token = Mint(adoption.Session, AdoptedLaunch, adoption.RootPid, adoption.RootStartUtc, adoption.Scope); }
            catch (InvalidOperationException e) { return Declined(e.Message); }
            pending.Remove(adoption);
            if (problem is not null) { return AgentClaimDecision.Refused($"the credential store is unavailable: {problem}"); }
            var issued = records.First(r => r.Credential.Session == adoption.Session).Credential;
            log($"agent credentials: issued credential {issued.Id} to adopted session {adoption.Session} as {adoption.Scope}");
            return new AgentClaimDecision(token, issued, null);
        }
    }

    AgentClaimDecision Declined(string why)
    {
        log($"agent credentials: refused agent.credential: {why}");
        return AgentClaimDecision.Refused(why);
    }

    // The person withdraws an adoption or the credential it gave. True when anything was there.
    public bool Revoke(string session, string why)
    {
        lock (gate)
        {
            var waiting = pending.RemoveAll(p => p.Session == session) > 0;
            if (waiting) { log($"agent credentials: adoption of session {session} withdrawn: {why}"); }
            return Remove(r => r.Credential.Kind == "session" && r.Credential.Session == session, why) || waiting;
        }
    }

    // Issues a credential for the process a launch started and returns the token, which is shown
    // once: the caller puts it in the child's environment and keeps nothing of it.
    public string Mint(string session, string launchId, int rootPid, DateTimeOffset rootStartUtc, string scope, int depth = 0, string[]? chain = null, TimeSpan? lifetime = null)
    {
        if (!AgentProtocol.Scopes.Contains(scope, StringComparer.Ordinal)) { throw new ArgumentException($"unknown agent scope '{scope}'", nameof(scope)); }
        if (!ValidSession(session)) { throw new ArgumentException("an agent credential is bound to a session id", nameof(session)); }
        var token = AgentProtocol.TokenPrefix + BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        var credential = new AgentCredential(BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(16)), session, launchId, rootPid, rootStartUtc, scope,
            depth, chain ?? [], now, now + (lifetime ?? TimeSpan.FromDays(DefaultDays)));
        lock (gate)
        {
            if (problem is not null) { throw new InvalidOperationException($"agent credentials are unavailable: {problem}"); }
            Prune(now);
            RevokeEnded(rootAlive);
            if (records.Count >= MaxCredentials) { throw new InvalidOperationException("too many live agent credentials"); }
            // One credential per session: a resume or a relaunch gets a new one and the old one is dead (§3.4).
            records.RemoveAll(r => r.Credential.Session == session);
            records.Add(new Record(Hash(token), credential));
            Save();
        }
        return token;
    }

    // A launch's token is issued before its process exists, so it is bound to none and answers to
    // nobody until Bind. Launched agents may read only: the agents policy (§5.1) widens that later.
    public string? Prepare(string session, string launchId) => Mint(session, launchId, 0, default, AgentProtocol.ScopeRead);

    // Called only by the person-authorized management path, never a remote/agent operation.
    // No token is returned. Both hashes stand until a successful fleet read retires the old one.
    public (AgentCredential Credential, string Path) MintScript(string name, TimeSpan? lifetime = null)
    {
        var now = time.GetUtcNow();
        var credential = AgentCredential.Script(BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(16)), machine, name, now, lifetime);
        lock (gate)
        {
            if (problem is not null) { throw new InvalidOperationException("agent credentials are unavailable"); }
            log($"agent credentials: issuing script credential {credential.Id} for {credential.Principal}");
            Prune(now);
            if (problem is not null) { throw new InvalidOperationException("agent credentials are unavailable"); }
            if (records.Count >= MaxCredentials) { throw new InvalidOperationException("too many live agent credentials"); }
            if (records.Count(r => r.Credential.Kind == "script" && r.Credential.Principal == credential.Principal) >= 2)
            {
                throw new InvalidOperationException("two script credentials are already live for this name; revoke before minting another");
            }
            var folder = PrivateFolder.Ensure(Path.Combine(Path.GetDirectoryName(path)!, "script-credentials"),
                "the script credential folder", "no script credential is issued");
            // Hashing the validated principal also keeps case-distinct names distinct on Windows.
            var final = Path.Combine(folder, Hash(credential.Principal!) + ".key");
            var temp = Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
            var clear = System.Text.Encoding.ASCII.GetBytes(AgentProtocol.TokenPrefix + BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(32)));
            try
            {
                using (var file = PrivateFolder.CreateNew(temp))
                {
                    file.Write(OperatingSystem.IsWindows() ? ProtectScript(clear) : clear);
                    file.Flush(true);
                }
                records.Add(new Record(Convert.ToHexString(SHA256.HashData(clear)), credential));
                // Commit the hash first: a crash can leave an unused hash, never a usable file
                // without its hash. The existing file is untouched until the atomic replacement.
                Save();
                File.Move(temp, final, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                if (records.RemoveAll(r => r.Credential.Id == credential.Id) > 0)
                {
                    try { Save(); }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { Fault("could not roll back script credential issuance"); }
                }
                log("agent credentials: script credential issuance failed; the previous script file was not replaced");
                throw new InvalidOperationException("could not store the script credential; no new credential was issued");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
                try { File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fault("could not remove the protected script credential temporary file"); }
            }
            return (credential, final);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static byte[] ProtectScript(byte[] clear) => ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);

    public void Bind(string launchId, int processId, DateTimeOffset startedAt)
    {
        lock (gate)
        {
            var i = records.FindIndex(r => r.Credential.Kind == "session" && r.Credential.LaunchId == launchId && r.Credential.RootPid == 0);
            if (i < 0) { return; }
            records[i] = records[i] with { Credential = records[i].Credential with { RootPid = processId, RootStartUtc = startedAt } };
            try { Save(); }
            catch (IOException e) { Fault($"could not save: {e.Message}"); }
            catch (UnauthorizedAccessException e) { Fault($"could not save: {e.Message}"); }
        }
    }

    public void Abandon(string launchId)
    {
        lock (gate) { Remove(r => r.Credential.Kind == "session" && r.Credential.LaunchId == launchId && r.Credential.RootPid == 0, "its launch did not start"); }
    }

    // Whether this ancestry may hold this token. Every refusal says why, for the log.
    public AgentHelloDecision Check(string? token, ProcessAncestry ancestry, bool agentCaller = false)
    {
        var presented = token is { Length: AgentProtocol.TokenLength } && token.StartsWith(AgentProtocol.TokenPrefix, StringComparison.Ordinal) ? Hash(token) : null;
        var now = time.GetUtcNow();
        AgentCredential? match = null;
        lock (gate)
        {
            if (problem is not null) { return Refused($"the credential store is unavailable: {problem}"); }
            // Compares every record, so how long the answer takes doesn't say which one matched.
            foreach (var r in records)
            {
                if (presented is not null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(r.Hash), Convert.FromHexString(presented))) { match = r.Credential; }
            }
            if (match is not null && match.ExpiresAt <= now)
            {
                Remove(r => r.Credential.Id == match.Id, "expired");
                match = null;
            }
        }
        if (match is null) { return Refused("no live credential matches that token"); }
        if (match.Kind == "script")
        {
            if (!ancestry.Complete || ancestry.Processes.Count == 0
                || ancestry.Processes.Any(p => p.Id <= 0 || p.StartedAt is null || string.IsNullOrWhiteSpace(p.Image)))
            { return Outside(match, "the script caller's ancestry could not be traced in full"); }
            lock (gate)
            {
                var agents = records.Where(r => r.Credential.Kind == "session" && r.Credential.RootPid > 0)
                    .Select(r => new KnownProcess(r.Credential.RootPid, r.Credential.RootStartUtc, "a credentialed session"))
                    .Concat(pending.Select(p => new KnownProcess(p.RootPid, p.RootStartUtc, "an adopted session"))).ToArray();
                if (agentCaller || AgentAncestry.Find(ancestry.Processes, agents) is not null)
                { return Outside(match, "a script credential cannot be used inside an agent session"); }
                return IsLive(match) ? new AgentHelloDecision(match, null) : Refused("no live credential matches that token");
            }
        }
        if (match.RootPid <= 0) { return Outside(match, "its launch has not started"); }
        if (!ancestry.Complete)
        {
            return Outside(match, "the caller's ancestry could only be read in part");
        }
        var bound = ancestry.Processes.FirstOrDefault(p => p.Id == match.RootPid);
        if (bound is null) { return Outside(match, "the session's process isn't among the caller's ancestors"); }
        // Both sides must say when it started: an id alone could be a later process given the same one.
        if (bound.StartedAt is not { } started || (started - match.RootStartUtc).Duration() > StartSlack)
        {
            return Outside(match, "the process with the session's id is not the one that started it");
        }
        // The ancestry was read when the pipe connected; the session may have ended since.
        if (!rootAlive(match.RootPid, match.RootStartUtc))
        {
            lock (gate) { Remove(r => r.Credential.Id == match.Id, "its session's process ended"); }
            return Outside(match, "the session's process has ended");
        }
        return new AgentHelloDecision(match, null);
    }

    // Whether the process with this id is still the one that started at that time.
    public static bool SameProcessAlive(int pid, DateTimeOffset startedUtc)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && (new DateTimeOffset(p.StartTime.ToUniversalTime()) - startedUtc).Duration() <= StartSlack;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    AgentHelloDecision Outside(AgentCredential credential, string why)
    {
        log($"agent credentials: credential presented from outside its session ({why}); credential {credential.Id}; principal {PrincipalOf(credential)}");
        return AgentHelloDecision.Refused($"credential presented from outside its session: {why}");
    }

    AgentHelloDecision Refused(string why)
    {
        log($"agent credentials: refused agent.hello: {why}");
        return AgentHelloDecision.Refused(why);
    }

    // Opens the agent connection a successful hello grants: from now on this channel is the
    // principal, for as long as its credential stands.
    public AgentHelloResult Accept(IProtocolChannel channel, AgentCredential credential)
    {
        var principal = PrincipalOf(credential);
        connections.AddOrUpdate(channel, new Connection(credential.Id, principal, credential.Kind == "script"));
        return new AgentHelloResult(principal, credential.Kind == "script" ? AgentProtocol.ScopeRead : credential.Scope);
    }

    // Keep the script restriction even after its record disappears: absence is never person authority.
    internal void CheckScriptOperation(IProtocolChannel channel, string op, Action<string>? refusalLog = null)
    {
        if (!connections.TryGetValue(channel, out var c) || !c.Script) { return; }
        var why = op is not (Fleet.FleetList.Op or Fleet.FleetList.FindOp)
            ? "script connections may call only fleet.list and fleet.find"
            : Of(channel) is null ? "the script credential is no longer live" : null;
        if (why is null) { return; }
        var line = $"refused {op} from {c.Principal}: {why}";
        log($"agent credentials: {line}");
        refusalLog?.Invoke($"local pipe: {line}");
        throw new InvalidOperationException($"refused: {why}");
    }

    internal void CompleteFleetRead(AgentCredential caller, object result)
    {
        if (caller.Kind != "script") { return; }
        // Size the full pipe response with the longest request id before committing rotation.
        if (ProtocolJson.ByteCount(new { re = long.MinValue, ok = true, result }) > SecureChannel.MaxMessageBytes)
        {
            log($"agent credentials: refused fleet read from {PrincipalOf(caller)}: response too large");
            throw new InvalidOperationException("refused: fleet response too large");
        }
        lock (gate)
        {
            if (!IsLive(caller))
            {
                log($"agent credentials: refused fleet read from {PrincipalOf(caller)}: the credential is no longer live");
                throw new InvalidOperationException("refused: the script credential is no longer live");
            }
            // List order survives reload and distinguishes rotations minted at the same clock tick.
            var newest = records.Last(r => r.Credential.Kind == "script" && r.Credential.Principal == caller.Principal);
            if (newest.Credential.Id != caller.Id) { return; }
            Remove(r => r.Credential.Kind == "script" && r.Credential.Principal == caller.Principal && r.Credential.Id != caller.Id, "rotated after a successful fleet read");
            if (problem is not null) { throw new InvalidOperationException("refused: could not persist script credential rotation"); }
        }
    }

    public string PrincipalOf(AgentCredential credential) =>
        credential.Kind == "script"
            ? credential.Principal ?? throw new ArgumentException("script credential has no principal", nameof(credential))
            : AgentProtocol.Principal(machine, credential.Session);

    // The credential behind this channel's agent connection, or null when it has none or it has
    // been revoked or has expired since.
    public AgentCredential? Of(IProtocolChannel channel)
    {
        if (!connections.TryGetValue(channel, out var c)) { return null; }
        var now = time.GetUtcNow();
        AgentCredential? found;
        lock (gate) { found = problem is null ? records.Select(r => r.Credential).FirstOrDefault(x => x.Id == c.CredentialId && x.ExpiresAt > now) : null; }
        if (found is { Kind: "session" } && !rootAlive(found.RootPid, found.RootStartUtc))
        {
            lock (gate) { Remove(r => r.Credential.Id == found.Id, "its session's process ended"); }
            return null;
        }
        return found;
    }

    // Whether this grant is still held, unexpired and its session process alive: the sender asks before it signs.
    public bool IsLive(AgentCredential credential)
    {
        var now = time.GetUtcNow();
        lock (gate) { if (problem is not null || !records.Any(r => r.Credential.Id == credential.Id && r.Credential.ExpiresAt > now)) { return false; } }
        return credential.Kind == "script" || rootAlive(credential.RootPid, credential.RootStartUtc);
    }

    public bool RevokeSession(string session, string why)
    {
        lock (gate) { return Remove(r => r.Credential.Kind == "session" && r.Credential.Session == session, why); }
    }

    public bool RevokeScript(string name)
    {
        if (!ValidScriptPart(name)) { throw new ArgumentException("invalid script name", nameof(name)); }
        var principal = $"script:{machine}/{name}";
        lock (gate)
        {
            if (problem is not null) { throw new InvalidOperationException("agent credentials are unavailable"); }
            var removed = Remove(r => r.Credential.Kind == "script" && r.Credential.Principal == principal, "revoked by a person at this machine");
            if (problem is not null) { throw new InvalidOperationException("could not save script revocation; the credential store is unavailable"); }
            return removed;
        }
    }

    // A session's process ended: whatever it held dies with it (§3.4 "Revocation").
    public int RevokeEnded(Func<int, DateTimeOffset, bool> sameProcessAlive)
    {
        lock (gate)
        {
            var before = records.Count;
            Remove(r => r.Credential.RootPid > 0 && !sameProcessAlive(r.Credential.RootPid, r.Credential.RootStartUtc), "its session's process ended");
            return before - records.Count;
        }
    }

    void Prune(DateTimeOffset now) => Remove(r => r.Credential.ExpiresAt <= now, "expired");

    bool Remove(Predicate<Record> which, string why)
    {
        var gone = records.Where(r => which(r)).ToList();
        if (gone.Count == 0) { return false; }
        records.RemoveAll(which);
        foreach (var r in gone) { log($"agent credentials: revoked {r.Credential.Id} for {PrincipalOf(r.Credential)}: {why}"); }
        try { Save(); }
        catch (IOException e) { Fault($"could not save: {e.Message}"); }
        catch (UnauthorizedAccessException e) { Fault($"could not save: {e.Message}"); }
        return true;
    }

    void Load()
    {
        if (!File.Exists(path)) { return; }
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4 * 1024 * 1024) { throw new JsonException("store exceeds its byte bound"); }
            var saved = JsonSerializer.Deserialize<Saved>(file, Json) ?? throw new JsonException("empty store");
            if (saved.Version != 1 || saved.Records is null || saved.Records.Length > MaxCredentials) { throw new JsonException("unsupported store"); }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in saved.Records)
            {
                if (r?.Credential is not { } c || r.Hash is not { Length: 64 } || !IsHex(r.Hash) || c.Id is not { Length: > 0 } || !ids.Add(c.Id)
                    || !AgentProtocol.Scopes.Contains(c.Scope, StringComparer.Ordinal) || c.Chain is null
                    || !(c.Kind == "session" ? ValidSession(c.Session) && c.Principal is null : c.Kind == "script" && ValidScript(c, machine)))
                {
                    throw new JsonException("invalid credential record");
                }
                records.Add(r);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            records.Clear();
            Fault($"could not read {FileName}: {e.Message}");
        }
    }

    void Fault(string why)
    {
        problem = why;
        log($"agent credentials: {why}; no agent credential is accepted or issued until it is fixed");
    }

    void Save()
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new Saved(1, [.. records]), Json));
        File.Move(temp, path, overwrite: true);
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));

    static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    internal static bool ValidScript(AgentCredential c, string machine)
    {
        var prefix = $"script:{machine}/";
        return ValidScriptPart(machine) && c.Principal is { Length: <= 128 } principal
            && principal.StartsWith(prefix, StringComparison.Ordinal) && ValidScriptPart(principal[prefix.Length..])
            && c.Session == "" && c.LaunchId == "" && c.RootPid == 0 && c.RootStartUtc == default
            && c.Scope == AgentProtocol.ScopeReadReply && c.Depth == 0 && c.Chain is { Length: 0 }
            && c.IssuedAt != default && c.ExpiresAt > c.IssuedAt
            && c.ExpiresAt - c.IssuedAt <= TimeSpan.FromDays(DefaultScriptDays);
    }

    internal static bool ValidScriptPart(string? value) => value is { Length: > 0 and <= 64 } && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // A session id is at most 36 characters (a UUID).
    static bool ValidSession(string? s) => s is { Length: > 0 and <= 36 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
