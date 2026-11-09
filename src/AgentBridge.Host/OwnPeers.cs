using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Migration;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed record OwnPeer(string DeviceId, string Account, string PublicKey, string Name, DateTimeOffset PinnedAt);

// A pin code is what `peers show` prints on the other host: its device id, account id and agent
// signing key, signed by that key. The person still compares the fingerprint. It carries no secret.
public static class OwnPeerPin
{
    static byte[] Payload(string deviceId, string account, byte[] key) =>
        Encoding.UTF8.GetBytes($"agent-bridge own peer pin v1|{deviceId}|{account}|{BlobFormat.Base64Url(key)}");

    public static bool Verify(byte[] key, byte[] data, byte[] sig)
    {
        try
        {
            P256KeyPair.AssertOnCurve(key);
            using var verify = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key[1..33], Y = key[33..] } });
            return verify.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return false; }
    }

    public static string Make(AgentSignKey key, string deviceId, string account) =>
        $"{deviceId}.{account}.{BlobFormat.Base64Url(key.PublicPoint)}.{BlobFormat.Base64Url(key.Sign(Payload(deviceId, account, key.PublicPoint)))}";

    // The parsed code, or null when it is malformed, off the curve or its signature does not verify.
    public static (string DeviceId, string Account, byte[] Key)? Parse(string code)
    {
        var parts = code.Split('.');
        if (parts.Length != 4 || !Wire.IsDeviceId(parts[0]) || !Wire.IsDeviceId(parts[1])) { return null; }
        var key = BlobFormat.FromBase64Url(parts[2]);
        var sig = BlobFormat.FromBase64Url(parts[3]);
        if (key is null || sig is null || key.Length != 65 || key[0] != 0x04) { return null; }
        return Verify(key, Payload(parts[0], parts[1], key), sig) ? (parts[0], parts[1], key) : null;
    }
}

// AP5-lite (temporary, off by default): the owner's other hosts this host trusts, pinned by a
// person at this machine; not the signed roster. With the mode off, or a missing, unreadable,
// unsigned or invalid file, no peer is trusted, and a peer of another account is ignored. Only
// OwnPeerCommands write it. Every read goes to the file, so a removal needs no restart.
public sealed class OwnPeers
{
    public const string FileName = "own-peers.json";
    public const int MaxPeers = 16;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8, WriteIndented = true,
    };

    sealed record Saved(int Version, OwnPeer[] Peers, string? Sig);

    readonly string folder;
    readonly string path;
    readonly Func<bool> modeOn;
    readonly Func<string?> account;

    public OwnPeers(string folder, Func<bool> modeOn, Func<string?> account)
    {
        this.folder = folder;
        path = Path.Combine(folder, FileName);
        this.modeOn = modeOn;
        this.account = account;
    }

    // The list is signed with this host's agent signing key, so a hand-edited file is refused.
    static byte[] SignedBytes(IEnumerable<OwnPeer> peers) =>
        Encoding.UTF8.GetBytes("agent-bridge own peers v1\n" + string.Join('\n', peers.Select(p => $"{p.DeviceId}|{p.Account}|{p.PublicKey}|{p.Name}|{p.PinnedAt:O}")));

    // One writer at a time across processes, so two removals cannot undo each other.
    T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, @"Local\agent-bridge-own-peers-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)).AsSpan(0, 8)));
        try { mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { }
        try { return action(); }
        finally { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
    }

    public bool Enabled => modeOn();

    // Short enough to compare by eye.
    public static string Fingerprint(byte[] publicKey) =>
        string.Join('-', Convert.ToHexString(SHA256.HashData(publicKey).AsSpan(0, 16)).Chunk(4).Select(c => new string(c)));

    // The pinned peers of this host's own account; empty when the mode is off or the file is untrusted.
    public IReadOnlyList<OwnPeer> List() =>
        modeOn() && account() is { } own && Read() is { } all ? [.. all.Where(p => p.Account == own)] : [];

    // True when the file exists but cannot be trusted.
    public bool Damaged => File.Exists(path) && Read() is null;

    // The pinned signing key for a sender's device id, or null: unknown, cleared, other account, or mode off.
    public byte[]? TrustedKey(string deviceId) =>
        List().FirstOrDefault(p => p.DeviceId == deviceId) is { } peer ? BlobFormat.FromBase64Url(peer.PublicKey) : null;

    public void Pin(string code, string name)
    {
        if (!modeOn()) { throw new InvalidOperationException("own-account messaging is off on this host"); }
        var own = account() ?? throw new InvalidOperationException("this host is not signed in, so it has no account to pin peers of");
        var (deviceId, peerAccount, key) = OwnPeerPin.Parse(code) ?? throw new ArgumentException("that pin code is not valid or is not signed by its key");
        if (peerAccount != own) { throw new InvalidOperationException("that host is on a different account"); }
        Locked(() =>
        {
            var peers = (Read() ?? throw new InvalidOperationException("the pinned peer file is unreadable; clear it first")).ToList();
            var encoded = BlobFormat.Base64Url(key);
            if (peers.Any(p => p.DeviceId == deviceId || p.PublicKey == encoded)) { throw new InvalidOperationException("that peer is already pinned"); }
            if (peers.Count >= MaxPeers) { throw new InvalidOperationException($"at most {MaxPeers} peers can be pinned"); }
            peers.Add(new OwnPeer(deviceId, peerAccount, encoded, name.Length > 64 ? name[..64] : name, DateTimeOffset.UtcNow));
            Write(peers);
            return 0;
        });
    }

    // Clears one peer, or all when deviceId is null; allowed with the mode off or a damaged file.
    public int Clear(string? deviceId) => Locked(() =>
    {
        var peers = Read() ?? [];
        var kept = deviceId is null ? [] : peers.Where(p => p.DeviceId != deviceId).ToList();
        Write(kept);
        return peers.Count - kept.Count;
    });

    List<OwnPeer>? Read()
    {
        if (!File.Exists(path)) { return []; }
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 64 * 1024) { return null; }
            var saved = JsonSerializer.Deserialize<Saved>(bytes, Json);
            if (saved is null || saved.Version != 1 || saved.Peers is null || saved.Peers.Length > MaxPeers) { return null; }
            foreach (var p in saved.Peers)
            {
                if (p is null || !Wire.IsDeviceId(p.DeviceId) || !Wire.IsDeviceId(p.Account) || p.PublicKey is null || p.Name is null) { return null; }
                var key = BlobFormat.FromBase64Url(p.PublicKey);
                if (key is null || key.Length != 65) { return null; }
                P256KeyPair.AssertOnCurve(key);
            }
            if (saved.Peers.Length > 0)
            {
                using var own = AgentSignKey.Open(folder);
                var sig = BlobFormat.FromBase64Url(saved.Sig);
                if (own is null || sig is null || !OwnPeerPin.Verify(own.PublicPoint, SignedBytes(saved.Peers), sig)) { return null; }
            }
            return [.. saved.Peers];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    void Write(List<OwnPeer> peers)
    {
        string? sig = null;
        if (peers.Count > 0)
        {
            using var own = AgentSignKey.Open(folder) ?? AgentSignKey.OpenOrCreate(folder, modeOn: true);
            sig = BlobFormat.Base64Url(own.Sign(SignedBytes(peers)));
        }
        DurableFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new Saved(1, [.. peers], sig), Json));
    }
}
