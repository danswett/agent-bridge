using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Migration;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed record TrustedPeerPin(string PublicKey, string Role, long Generation, DateTimeOffset ExpiresAt, bool Revoked);
public sealed record TrustedPeerLease(string Epoch, string PublicKey, string Role, long Generation);

// Local, transitional authority for the opt-in host floor, not the signed account roster.
// The enforcement switch deliberately does not live in this file.
public sealed class TrustedPeerStore
{
    public const string FileName = "trusted-peers.json";
    public const int MaxPins = 256;
    public const string Person = "person";
    public const string Host = "host";
    public const string Service = "service";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
        WriteIndented = true,
    };

    readonly object gate = new();
    readonly string path;
    readonly string blockedPath;
    readonly string host;
    readonly Func<string?> account;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly Dictionary<string, (long Started, TimeSpan Remaining)> deadlines = new(StringComparer.Ordinal);
    Saved? saved;
    DateTimeOffset observed;
    string? problem;

    sealed record Saved(int Version, string Epoch, string Host, string Account, DateTimeOffset ObservedAt, bool Blocked, TrustedPeerPin[] Pins);

    public TrustedPeerStore(string folder, byte[] hostKey, Func<string?> account, TimeProvider? time = null, Action<string>? log = null)
    {
        P256KeyPair.AssertOnCurve(hostKey);
        path = Path.Combine(folder, FileName);
        blockedPath = Path.Combine(folder, "trusted-peers.blocked");
        host = BlobFormat.Base64Url(hostKey);
        this.account = account;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        observed = this.time.GetUtcNow();
        Load();
    }

    public string? Problem { get { lock (gate) { return problem; } } }

    void Fault(string why)
    {
        problem = why;
        log($"trusted peers: {why}; remote authority denied until an explicit owner reset");
    }

    static string Key(byte[] value)
    {
        P256KeyPair.AssertOnCurve(value);
        return BlobFormat.Base64Url(value);
    }

    static byte[] ParseKey(string value)
    {
        var bytes = BlobFormat.FromBase64Url(value) ?? throw new JsonException("invalid full public key");
        P256KeyPair.AssertOnCurve(bytes);
        if (BlobFormat.Base64Url(bytes) != value) { throw new JsonException("noncanonical full public key"); }
        return bytes;
    }

    void Load()
    {
        try
        {
            if (BlockedOnDisk())
            {
                Fault("the trust store is durably blocked");
                return;
            }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 256 * 1024) { throw new JsonException("store exceeds its byte bound"); }
            var value = JsonSerializer.Deserialize<Saved>(file, Json) ?? throw new JsonException("empty store");
            if (value.Version != 1 || !Guid.TryParseExact(value.Epoch, "D", out _) || value.Host != host
                || string.IsNullOrEmpty(value.Account) || value.Pins is null || value.Pins.Length > MaxPins
                || value.ObservedAt == default)
            {
                throw new JsonException("unsupported or incorrectly bound store");
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pin in value.Pins)
            {
                if (pin is null || pin.PublicKey is null || pin.PublicKey == host || pin.Role is not (Person or Host or Service)
                    || pin.Generation < 1 || pin.ExpiresAt == default)
                {
                    throw new JsonException("invalid pin");
                }
                var key = ParseKey(pin.PublicKey);
                if (!keys.Add(pin.PublicKey) || !ids.Add(Wire.DeviceId(key))) { throw new JsonException("ambiguous pin"); }
                deadlines[pin.PublicKey] = (time.GetTimestamp(), pin.ExpiresAt - observed);
            }
            saved = value;
            if (observed < value.ObservedAt)
            {
                Save(value with { Blocked = true });
                Fault("the clock moved backwards");
            }
            else if (value.Blocked) { Fault("the store is blocked"); }
            observed = observed > value.ObservedAt ? observed : value.ObservedAt;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            Fault("the trust store is missing, unreadable or invalid");
        }
    }

    bool BlockedOnDisk()
    {
        try { _ = File.GetAttributes(blockedPath); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    void Save(Saved value)
    {
        var marked = false;
        try
        {
            // Write intent before replacing the grant file, so a failed revoke cannot revive on restart.
            DurableFile.Write(blockedPath, [1]);
            marked = true;
            DurableFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            if (!value.Blocked) { File.Delete(blockedPath); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var why = marked
                ? "the trust store mutation did not complete; its durable block requires an explicit owner reset"
                : "the trust store block could not be durably written; revocation NOT durable; do not restart before a successful owner reset";
            Fault(why);
            throw new IOException(why, e);
        }
        saved = value;
    }

    bool Refresh()
    {
        if (problem is not null || saved is null) { return false; }
        var now = time.GetUtcNow();
        try
        {
            if (now < observed)
            {
                Save(saved with { Blocked = true, ObservedAt = observed });
                Fault("the clock moved backwards");
                return false;
            }
            observed = now;
            var pins = saved.Pins.Select(p =>
            {
                var deadline = deadlines[p.PublicKey];
                return !p.Revoked && (now >= p.ExpiresAt || time.GetElapsedTime(deadline.Started) >= deadline.Remaining)
                    ? p with { Revoked = true }
                    : p;
            }).ToArray();
            if (!pins.SequenceEqual(saved.Pins)) { Save(saved with { Pins = pins, ObservedAt = observed }); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Save logged the failure and latched the deny-all state.
        }
        return true;
    }

    bool Bound(string? connectionAccount) =>
        saved is { } s && s.Account == account() && s.Account == connectionAccount;

    TrustedPeerLease? Find(string key, string? connectionAccount)
    {
        if (!Refresh() || !Bound(connectionAccount)) { return null; }
        var pin = saved!.Pins.FirstOrDefault(p => p.PublicKey == key && !p.Revoked);
        return pin is null ? null : new TrustedPeerLease(saved.Epoch, pin.PublicKey, pin.Role, pin.Generation);
    }

    public TrustedPeerLease? Find(byte[] publicKey, string? connectionAccount)
    {
        lock (gate) { return Find(Key(publicKey), connectionAccount); }
    }

    public TrustedPeerLease? FindEncoded(string publicKey, string? connectionAccount)
    {
        lock (gate) { return Find(publicKey, connectionAccount); }
    }

    public TrustedPeerLease? FindDevice(string id)
    {
        lock (gate)
        {
            if (!Refresh() || !Bound(account())) { return null; }
            var pin = saved!.Pins.FirstOrDefault(p => Wire.DeviceId(ParseKey(p.PublicKey)) == id);
            return pin is null ? null : Find(pin.PublicKey, account());
        }
    }

    public bool Current(TrustedPeerLease lease, string? connectionAccount)
    {
        lock (gate) { return Find(lease.PublicKey, connectionAccount) == lease; }
    }

    public IReadOnlyList<TrustedPeerPin> Pins
    {
        get { lock (gate) { Refresh(); return saved?.Pins.ToArray() ?? []; } }
    }

    // Reset is an explicit app-only owner action, never automatic recovery of a bad file.
    public void Reset()
    {
        lock (gate)
        {
            var owner = account();
            if (string.IsNullOrEmpty(owner)) { throw new InvalidOperationException("sign in before seeding trust"); }
            var now = time.GetUtcNow();
            Save(new Saved(1, Guid.NewGuid().ToString("D"), host, owner, now, false, []));
            problem = null;
            observed = now;
            deadlines.Clear();
        }
    }

    public TrustedPeerPin Put(byte[] publicKey, string role, DateTimeOffset expiresAt)
    {
        var key = Key(publicKey);
        if (role is not (Person or Host or Service)) { throw new ArgumentException("unknown peer role"); }
        lock (gate)
        {
            if (!Refresh() || !Bound(account())) { throw new InvalidOperationException("trust is unavailable; an explicit owner reset is required"); }
            if (expiresAt <= observed) { throw new ArgumentException("the pin must expire in the future"); }
            if (key == host) { throw new ArgumentException("the host cannot pin itself as a remote peer"); }
            var old = saved!.Pins.FirstOrDefault(p => p.PublicKey == key);
            if (old is null && saved.Pins.Length >= MaxPins) { throw new InvalidOperationException("the trust store is full"); }
            if (saved.Pins.Any(p => p.PublicKey != key && Wire.DeviceId(ParseKey(p.PublicKey)) == Wire.DeviceId(publicKey)))
            {
                throw new ArgumentException("the full key has an ambiguous device id");
            }
            var pin = new TrustedPeerPin(key, role, checked((old?.Generation ?? 0) + 1), expiresAt, false);
            Save(saved with { Pins = [.. saved.Pins.Where(p => p.PublicKey != key), pin], ObservedAt = observed });
            deadlines[key] = (time.GetTimestamp(), expiresAt - observed);
            return pin;
        }
    }

    public void Revoke(byte[] publicKey)
    {
        var key = Key(publicKey);
        lock (gate)
        {
            if (!Refresh() || !Bound(account())) { throw new InvalidOperationException("trust is unavailable; an explicit owner reset is required"); }
            if (!saved!.Pins.Any(p => p.PublicKey == key)) { throw new ArgumentException("the full key is not pinned"); }
            Save(saved with
            {
                Pins = [.. saved.Pins.Select(p => p.PublicKey == key ? p with { Revoked = true } : p)],
                ObservedAt = observed,
            });
        }
    }
}
