using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

// Authenticated by this profile's existing device key; a JSON edit cannot invent a launch.
public sealed class LiteLaunchLog : ILiteLaunches
{
    public const string FileName = "lite-launches.bin";
    const int MaxBytes = 1024 * 1024;
    readonly Lock gate = new();
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly Func<string, SessionProcess?> process;
    readonly string? path;
    readonly P256KeyPair? key;
    readonly Action<string> log;
    internal sealed record Entry(string Permissions, SessionProcess Process);

    public LiteLaunchLog(Func<string, SessionProcess?> process, string? folder = null, P256KeyPair? key = null, Action<string>? log = null)
    {
        this.process = process; this.key = key; this.log = log ?? (_ => { });
        path = folder is null ? null : Path.Combine(folder, FileName);
        if (path is null) { return; }
        try
        {
            byte[] bytes;
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length is < 32 or > MaxBytes) { throw new InvalidDataException(); }
                bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            }
            var body = bytes[32..];
            if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), Mac(body))) { throw new InvalidDataException(); }
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(body) ?? throw new InvalidDataException();
            if (saved.Any(e => string.IsNullOrWhiteSpace(e.Key) || e.Key.Length > 1024 || e.Value is not { Process: { Id: > 0, StartedAt: not null } }
                || e.Value.Permissions is not (LaunchProtocol.Ask or LaunchProtocol.AllowAll))) { throw new InvalidDataException(); }
            foreach (var entry in saved) { entries.Add(entry.Key, entry.Value); }
            Prune();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException)
        { entries.Clear(); this.log("lite launch record could not be verified or read; no saved session is a reply target"); }
    }

    public void Launched(string session, LaunchedWith with)
    {
        lock (gate)
        {
            Prune();
            if (process(session) is not { Id: > 0, StartedAt: not null } bound || with.Permissions is not (LaunchProtocol.Ask or LaunchProtocol.AllowAll))
            { log("lite launch record: launch has no verified process or permission mode; not a reply target"); return; }
            entries[session] = new(with.Permissions, bound);
            Save();
        }
    }

    public string? PermissionsOf(string session) => Capture(session)?.Permissions;

    internal Entry? Capture(string session)
    {
        lock (gate) { Prune(); return entries.GetValueOrDefault(session); }
    }

    public void Prune()
    {
        lock (gate)
        {
            var dead = entries.Where(e => process(e.Key) != e.Value.Process).Select(e => e.Key).ToArray();
            foreach (var id in dead) { entries.Remove(id); }
            if (dead.Length > 0) { Save(); }
        }
    }

    byte[] Mac(byte[] body) => DeviceMacFile.Mac(body, key, "agent-bridge lite launch log v1"u8);

    void Save()
    {
        if (path is null) { return; }
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (body.Length > MaxBytes - 32) { throw new IOException("launch record is full"); }
            DeviceMacFile.Write(path, [.. Mac(body), .. body], log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or System.Security.SecurityException)
        { entries.Clear(); log("lite launch record could not be saved; no recorded session is a reply target"); }
    }
}
