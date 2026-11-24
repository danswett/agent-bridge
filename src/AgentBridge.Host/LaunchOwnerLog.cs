using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Who launched a session, written by the host at launch from the AUTHENTICATED principal of the launch call (never a message
// body), so the session watch can still name the owner after the 15 minute launch ledger has forgotten it
// (docs/design/session-watch.md, #4262). Only an agent principal is an owner: a person, the app or a script leaves none.
// Authenticated by the device key like LiteLaunchLog, so a JSON edit cannot invent an owner.
public sealed class LaunchOwnerLog
{
    public const string FileName = "launch-owners.bin";
    const int MaxBytes = 1024 * 1024, MaxEntries = 4096;
    static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    readonly Lock gate = new();
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly string path;
    readonly P256KeyPair key;
    readonly Action<string> log;
    sealed record Entry(string Principal, DateTimeOffset LastLive);

    public LaunchOwnerLog(string folder, P256KeyPair key, Action<string> log)
    {
        this.key = key; this.log = log;
        path = Path.Combine(folder, FileName);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is < 32 or > MaxBytes) { throw new InvalidDataException(); }
            var body = bytes[32..];
            if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), Mac(body))) { throw new InvalidDataException(); }
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(body) ?? throw new InvalidDataException();
            if (saved.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Value is null || !IsAgent(e.Value.Principal))) { throw new InvalidDataException(); }
            foreach (var e in saved) { entries.Add(e.Key, e.Value); }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException)
        { entries.Clear(); this.log("launch owner record could not be verified or read; no session has a recorded owner"); }
    }

    // "agent:<device id>/<session>": the form the host's own authenticated launch call carries.
    static bool IsAgent(string? principal) =>
        principal is { Length: >= 40 and <= 256 } p && p.StartsWith("agent:", StringComparison.Ordinal)
        && p.IndexOf('/') is 38 && Wire.IsDeviceId(p[6..38]);

    public void Record(string session, string principal, DateTimeOffset now)
    {
        if (!IsAgent(principal) || string.IsNullOrWhiteSpace(session)) { return; }
        lock (gate)
        {
            if (entries.Count >= MaxEntries && !entries.ContainsKey(session)) { log("launch owner record is full; this session has no recorded owner"); return; }
            entries[session] = new(principal, now);
            Save();
        }
    }

    public string? OwnerOf(string session) { lock (gate) { return entries.GetValueOrDefault(session)?.Principal; } }

    // A live session keeps its record; one not live for 7 days loses it.
    public void Prune(Func<string, bool> isLive, DateTimeOffset now)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var (session, entry) in entries.ToArray())
            {
                if (isLive(session)) { entries[session] = entry with { LastLive = now }; changed = true; }
                else if (now - entry.LastLive > Keep) { entries.Remove(session); changed = true; }
            }
            if (changed) { Save(); }
        }
    }

    byte[] Mac(byte[] body) => DeviceMacFile.Mac(body, key, "agent-bridge launch owner log v1"u8);

    void Save()
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (body.Length > MaxBytes - 32) { throw new IOException("launch owner record is full"); }
            DeviceMacFile.Write(path, [.. Mac(body), .. body], log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or System.Security.SecurityException)
        { entries.Clear(); log("launch owner record could not be saved; no session has a recorded owner"); }
    }
}
