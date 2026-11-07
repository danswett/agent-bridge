using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What a credential is bound to (design/agent-principals.md §3.3). Id names the grant in logs and
// audit and is never the secret.
public sealed record AgentCredential(string Id, string Session, string LaunchId, int RootPid, DateTimeOffset RootStartUtc,
    string Scope, int Depth, string[] Chain, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public sealed record AgentHelloDecision(AgentCredential? Accepted, string? Refusal)
{
    public static AgentHelloDecision Refused(string why) => new(null, why);
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
public sealed class AgentCredentials
{
    public const string FileName = "agent-credentials.json";
    public const int MaxCredentials = 1024;
    public const int DefaultDays = 7;

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

    sealed record Connection(string CredentialId, string Principal);

    public AgentCredentials(string folder, string machine, TimeProvider? time = null, Action<string>? log = null, Func<int, DateTimeOffset, bool>? rootAlive = null)
    {
        this.rootAlive = rootAlive ?? ((_, _) => true);
        path = Path.Combine(folder, FileName);
        this.machine = machine;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        Load();
    }

    public string? Problem { get { lock (gate) { return problem; } } }

    public int Count { get { lock (gate) { return records.Count; } } }

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
            if (records.Count >= MaxCredentials) { throw new InvalidOperationException("too many live agent credentials"); }
            // One credential per session: a resume or a relaunch gets a new one and the old one is dead (§3.4).
            records.RemoveAll(r => r.Credential.Session == session);
            records.Add(new Record(Hash(token), credential));
            Save();
        }
        return token;
    }

    // Whether this ancestry may hold this token. Every refusal says why, for the log.
    public AgentHelloDecision Check(string? token, ProcessAncestry ancestry)
    {
        var presented = token is { Length: AgentProtocol.TokenLength } && token.StartsWith(AgentProtocol.TokenPrefix, StringComparison.Ordinal) ? Hash(token) : null;
        var now = time.GetUtcNow();
        AgentCredential? match = null;
        lock (gate)
        {
            if (problem is not null) { return AgentHelloDecision.Refused($"the credential store is unavailable: {problem}"); }
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
        if (match is null) { return AgentHelloDecision.Refused("no live credential matches that token"); }
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
        log($"agent credentials: credential presented from outside its session ({why}); credential {credential.Id}");
        return AgentHelloDecision.Refused($"credential presented from outside its session: {why}");
    }

    // Opens the agent connection a successful hello grants: from now on this channel is the
    // principal, for as long as its credential stands.
    public AgentHelloResult Accept(IProtocolChannel channel, AgentCredential credential)
    {
        var principal = AgentProtocol.Principal(machine, credential.Session);
        connections.AddOrUpdate(channel, new Connection(credential.Id, principal));
        return new AgentHelloResult(principal, credential.Scope);
    }

    // The credential behind this channel's agent connection, or null when it has none or it has
    // been revoked or has expired since.
    public AgentCredential? Of(IProtocolChannel channel)
    {
        if (!connections.TryGetValue(channel, out var c)) { return null; }
        var now = time.GetUtcNow();
        AgentCredential? found;
        lock (gate) { found = records.Select(r => r.Credential).FirstOrDefault(x => x.Id == c.CredentialId && x.ExpiresAt > now); }
        if (found is not null && !rootAlive(found.RootPid, found.RootStartUtc))
        {
            lock (gate) { Remove(r => r.Credential.Id == found.Id, "its session's process ended"); }
            return null;
        }
        return found;
    }

    public bool RevokeSession(string session, string why)
    {
        lock (gate) { return Remove(r => r.Credential.Session == session, why); }
    }

    // A session's process ended: whatever it held dies with it (§3.4 "Revocation").
    public int RevokeEnded(Func<int, DateTimeOffset, bool> sameProcessAlive)
    {
        lock (gate)
        {
            var before = records.Count;
            Remove(r => !sameProcessAlive(r.Credential.RootPid, r.Credential.RootStartUtc), "its session's process ended");
            return before - records.Count;
        }
    }

    void Prune(DateTimeOffset now) => Remove(r => r.Credential.ExpiresAt <= now, "expired");

    bool Remove(Predicate<Record> which, string why)
    {
        var gone = records.Where(r => which(r)).ToList();
        if (gone.Count == 0) { return false; }
        records.RemoveAll(which);
        foreach (var r in gone) { log($"agent credentials: revoked {r.Credential.Id} for session {r.Credential.Session}: {why}"); }
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
                    || !ValidSession(c.Session) || !AgentProtocol.Scopes.Contains(c.Scope, StringComparer.Ordinal) || c.Chain is null)
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

    // A session id is at most 36 characters (a UUID).
    static bool ValidSession(string? s) => s is { Length: > 0 and <= 36 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
