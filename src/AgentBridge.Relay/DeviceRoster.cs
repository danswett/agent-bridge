using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.Relay;

// An account's signed device roster (docs/protocol/devices.md): an append-only log of entries
// signed by the account's own devices. The relay is an untrusted store (design §3.6): it keeps an
// entry only if it is valid against its own copy, so a stranger cannot fill the log, but a host
// verifies what it is given for itself and never takes the relay's word. Nothing here decides who
// may connect; mode is "observe" and the relay serves every account the same.
//
// Kept apart from MachineRoster (the last-seen list of offline machines): that one is disposable
// and expires, this one is what an account's trust rests on.
public sealed class DeviceRoster(RelayOptions o, RelayMetrics metrics, ILogger<DeviceRoster> log, IRosterStore? store)
{
    public const string Mode = "observe";
    // One append carries a few entries (an add, its confirmation), never a whole history.
    public const int MaxPerAppend = 16;
    // The whole stored log: the library's own entry cap, in canonical form, bounded again by bytes.
    public const int MaxStoredBytes = 224 * 1024;
    const int Retries = 5;

    // Appends waiting on one account's lock, beyond the one running, are refused rather than queued.
    public const int MaxWaiters = 4;

    sealed class Gate { public readonly SemaphoreSlim Lock = new(1, 1); public int Waiting; }

    readonly ConcurrentDictionary<string, Gate> locks = new();
    readonly byte[] nameKey = DeriveKey(o, store);

    public bool Enabled => store is not null;

    static byte[] DeriveKey(RelayOptions o, IRosterStore? store)
    {
        if (store is not null and not MemoryRosterStore && string.IsNullOrEmpty(o.TokenKey))
        {
            // Names from a per-process key could never be found again after a restart.
            throw new InvalidOperationException("Relay:TokenKey is required for a stored device roster");
        }
        var root = string.IsNullOrEmpty(o.TokenKey) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(o.TokenKey);
        return HMACSHA256.HashData(root, "agent-bridge device roster name v1"u8);
    }

    public static IRosterStore? CreateStore(RosterOptions o) => o.DevicesStore ?? o.Backend.ToLowerInvariant() switch
    {
        "" or "memory" => new MemoryRosterStore(),
        "off" or "none" => null,
        "azure" => new AzureRosterStore(o, o.DevicesContainer),
        var other => throw new InvalidOperationException($"Relay:Roster:Backend {other} is not one of azure, memory, off or empty"),
    };

    string NameOf(string accountId) =>
        "v1/" + Convert.ToHexStringLower(HMACSHA256.HashData(nameKey, Encoding.UTF8.GetBytes(accountId))) + ".json";

    sealed record Stored(int V, string Generation, List<string> Entries);

    public sealed record Snapshot(string Mode, string? Generation, IReadOnlyList<string> Entries);
    public sealed record Result(string Hash, string Verdict, string? Reason);

    public async Task<Snapshot?> GetAsync(string accountId)
    {
        if (store is null) { return null; }
        try
        {
            using var cts = new CancellationTokenSource(o.Roster.StoreTimeout);
            var read = await store.ReadAsync(NameOf(accountId), cts.Token);
            var s = Decode(read);
            metrics.Roster("devices-get", "ok");
            return new Snapshot(Mode, s?.Generation, s?.Entries ?? []);
        }
        catch (Exception e)
        {
            metrics.Roster("devices-get", "error");
            log.LogWarning(e, "device roster read failed");
            return null;
        }
    }

    static Stored? Decode(StoredRoster? read) =>
        read is null ? null : JsonSerializer.Deserialize<Stored>(read.Body, Json) switch
        {
            { Entries: not null, Generation: not null } s => s,
            _ => throw new InvalidOperationException("stored device roster is malformed"),
        };

    // The reply with the property names docs/protocol/devices.md documents.
    public static object AppendReply(long id, IReadOnlyList<Result> results) => new
    {
        t = "roster-append-result",
        id,
        results = results.Select(r => new { hash = r.Hash, verdict = r.Verdict, reason = r.Reason }).ToList(),
    };

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Offers the entries, in order, to the account's log; null when the store could not be reached
    // (the caller says so, and nothing was kept).
    public async Task<IReadOnlyList<Result>?> AppendAsync(string accountId, IReadOnlyList<string> wrappers)
    {
        if (store is null) { return null; }
        var gate = locks.GetOrAdd(accountId, _ => new Gate());
        if (Interlocked.Increment(ref gate.Waiting) > MaxWaiters + 1)
        {
            Interlocked.Decrement(ref gate.Waiting);
            metrics.Roster("devices-append", "busy");
            return null;
        }
        await gate.Lock.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < Retries; attempt++)
            {
                using var cts = new CancellationTokenSource(o.Roster.StoreTimeout);
                var name = NameOf(accountId);
                StoredRoster? read;
                try { read = await store.ReadAsync(name, cts.Token); }
                catch (Exception e)
                {
                    metrics.Roster("devices-append", "error");
                    log.LogWarning(e, "device roster read failed");
                    return null;
                }
                IReadOnlyList<Result> results;
                Stored? next;
                try { (results, next) = Apply(accountId, Decode(read), wrappers); }
                catch (Exception e)
                {
                    metrics.Roster("devices-append", "corrupt");
                    log.LogError(e, "device roster failed to decode or reconstruct");
                    return null;
                }
                if (next is null)
                {
                    metrics.Roster("devices-append", "none");
                    return results;
                }
                var body = JsonSerializer.SerializeToUtf8Bytes(next, Json);
                if (body.Length > MaxStoredBytes)
                {
                    metrics.Roster("devices-append", "full");
                    return [.. results.Select(r => r.Verdict is "accepted" or "quarantined" ? r with { Verdict = "refused", Reason = Invalid.Cap } : r)];
                }
                try
                {
                    var written = await store.WriteAsync(name, body, read?.ETag, cts.Token);
                    if (written is not null)
                    {
                        metrics.Roster("devices-append", "ok");
                        return results;
                    }
                }
                catch (Exception e)
                {
                    metrics.Roster("devices-append", "error");
                    log.LogWarning(e, "device roster write failed");
                    return null;
                }
            }
            metrics.Roster("devices-append", "conflict");
            return null;
        }
        finally
        {
            gate.Lock.Release();
            Interlocked.Decrement(ref gate.Waiting);
        }
    }

    // The stored log with the new entries offered to it. next is null when nothing is to be kept.
    (IReadOnlyList<Result> Results, Stored? Next) Apply(string accountId, Stored? stored, IReadOnlyList<string> wrappers)
    {
        var results = new List<Result>(wrappers.Count);
        var parsed = new List<(int Index, Entry Entry)>();
        for (var i = 0; i < wrappers.Count; i++)
        {
            try
            {
                var e = Entry.Parse(Encoding.UTF8.GetBytes(wrappers[i]));
                parsed.Add((i, e));
                results.Add(new Result(Codec.Base64Url(e.Hash), "", null));
            }
            catch (RosterFormatException e)
            {
                results.Add(new Result("", "refused", e.Code));
            }
        }
        if (parsed.Count == 0) { return (results, null); }

        // Everything offered is already kept: nothing to verify, so skip the replay.
        if (stored is not null && parsed.Count == wrappers.Count)
        {
            var have = new HashSet<string>(stored.Entries);
            if (parsed.All(p => have.Contains(p.Entry.Canonical())))
            {
                return (parsed.Select(p => new Result(Codec.Base64Url(p.Entry.Hash), "duplicate", null)).ToList(), null);
            }
        }
        var generation = stored?.Generation;
        if (generation is null)
        {
            // The first entry fixes the log: it must be this account's genesis.
            var first = parsed[0].Entry.Body;
            if (first.Op != EntryOp.Genesis || first.Account != accountId)
            {
                return (Refuse(results, parsed, first.Op != EntryOp.Genesis ? Invalid.Genesis : Invalid.Account), null);
            }
            generation = first.Generation;
        }
        var verifier = new RosterVerifier(accountId, generation, () => o.Roster.Time.GetUtcNow() - DateTimeOffset.UnixEpoch);
        var kept = new List<string>(stored?.Entries ?? []);
        var replay = kept.Select(w => Entry.Parse(Encoding.UTF8.GetBytes(w))).ToList();
        // Stored entries were valid when kept; offering them again rebuilds the same state. One
        // that no longer holds means the copy cannot be trusted, so nothing is added to it.
        verifier.Offer(replay);
        if (replay.Any(e => !verifier.Holds(e.Hash))) { throw new InvalidOperationException("stored device roster does not reconstruct"); }
        var changed = false;
        // Entries set aside over a budget or cap: a later removal in this append may need them as
        // evidence, and then they are kept with it, ahead of it.
        var aside = new List<(int Index, Entry Entry)>();
        foreach (var (index, entry) in parsed)
        {
            var offered = verifier.Offer(entry)[0];
            var verdict = offered.Verdict.ToString().ToLowerInvariant();
            results[index] = new Result(Codec.Base64Url(offered.Hash), verdict, offered.Reason);
            if (offered.Verdict is Verdict.Accepted or Verdict.Quarantined)
            {
                if (entry.Body.Op == EntryOp.Remove)
                {
                    foreach (var (ai, ae) in aside.Where(a => verifier.Holds(a.Entry.Hash)).ToList())
                    {
                        kept.Add(ae.Canonical());
                        results[ai] = new Result(Codec.Base64Url(ae.Hash), "accepted", Invalid.Evidence);
                        aside.RemoveAll(a => a.Index == ai);
                    }
                }
                kept.Add(entry.Canonical());
                changed = true;
            }
            else if (offered.Verdict == Verdict.Refused && verifier.Graph.Find(entry.Hash) is not null)
            {
                aside.Add((index, entry));
            }
        }
        return (results, changed ? new Stored(1, generation, kept) : null);
    }

    static List<Result> Refuse(List<Result> results, List<(int Index, Entry Entry)> parsed, string reason)
    {
        foreach (var (index, entry) in parsed) { results[index] = new Result(Codec.Base64Url(entry.Hash), "refused", reason); }
        return results;
    }
}
