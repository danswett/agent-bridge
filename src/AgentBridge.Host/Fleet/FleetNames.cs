using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Migration;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

// Host-owned display names, never populated from agent homes or session titles.
public sealed class FleetNames
{
    public const string Op = "session.rename";
    public const string FileName = "fleet-names.json";
    readonly string path;
    readonly object gate = new();
    Dictionary<string, string> names;

    public FleetNames(string folder)
    {
        path = Path.Combine(folder, FileName);
        names = Load(path);
    }

    public string? Get(string id) { lock (gate) { return names.GetValueOrDefault(id); } }

    public object Rename(ISessionSource source, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())
            || !data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("session.rename needs an id and name");
        }
        var session = source.Get(id.GetString()!) ?? throw new ArgumentException("session.rename: session is not listed");
        var value = Normalize(name.GetString()!);
        lock (gate)
        {
            var next = new Dictionary<string, string>(names, StringComparer.Ordinal);
            if (value.Length == 0) { next.Remove(session.Id); }
            else { next[session.Id] = value; }
            // Publish the in-memory change only after the durable replacement succeeds.
            DurableFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, ProtocolJson.Options));
            names = next;
        }
        return new { id = session.Id, name = value };
    }

    static string Normalize(string name)
    {
        var value = name.Trim();
        if (value.Length > 0 && !IsValidName(value))
        {
            throw new ArgumentException("session.rename name must be 1 to 64 of A-Za-z0-9 space _.+()-; empty clears it");
        }
        return value;
    }

    internal static bool IsValidName(string value) => value.Length is > 0 and <= 64
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '.' or '+' or '(' or ')' or '-');

    static Dictionary<string, string> Load(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)
            ?? throw new InvalidDataException("fleet-names.json must contain a session-id-to-name object");
        foreach (var (id, name) in saved)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || name != Normalize(name))
            {
                throw new InvalidDataException("fleet-names.json contains an invalid fleet-name setting");
            }
        }
        return saved;
    }
}
