using System.Text.Json;
using AgentBridge.Host.Migration;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed class SessionDisplayTitles
{
    const string FileName = "display-titles.json";
    readonly string path;
    readonly object gate = new();
    Dictionary<string, Saved> titles;
    sealed record Saved(string? Title, DateTimeOffset UpdatedAt);

    public SessionDisplayTitles(string folder)
    {
        path = Path.Combine(folder, FileName);
        try
        {
            titles = JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllBytes(path), ProtocolJson.Options)
                ?? throw new InvalidDataException("display-titles.json must contain a session-id-to-title object");
        }
        catch (FileNotFoundException) { titles = new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { titles = new(StringComparer.Ordinal); }
        if (titles.Any(p => string.IsNullOrEmpty(p.Key) || p.Value is null || p.Value.UpdatedAt == default
            || (p.Value.Title is not null && !Session.IsDisplayTitle(p.Value.Title))))
        { throw new InvalidDataException("display-titles.json contains an invalid display-title setting"); }
    }

    public string? Get(string id) { lock (gate) { return titles.GetValueOrDefault(id)?.Title; } }

    public Session Apply(Session session)
    {
        lock (gate)
        {
            return titles.TryGetValue(session.Id, out var saved) ? session with
            {
                DisplayTitle = saved.Title,
                UpdatedAt = saved.UpdatedAt > session.UpdatedAt ? saved.UpdatedAt : session.UpdatedAt,
            } : session;
        }
    }

    public void Set(string id, string title, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(id)) { throw new ArgumentException("a display title needs a session id"); }
        var value = string.IsNullOrWhiteSpace(title) ? null : title;
        if (value is not null && !Session.IsDisplayTitle(value))
        { throw new ArgumentException("display title must be 1 to 120 visible plain-text characters; blank clears it"); }
        lock (gate)
        {
            if (titles.TryGetValue(id, out var prior) && at <= prior.UpdatedAt) { at = prior.UpdatedAt.AddMilliseconds(1); }
            var next = new Dictionary<string, Saved>(titles, StringComparer.Ordinal) { [id] = new(value, at) };
            DurableFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, ProtocolJson.Options));
            titles = next;
        }
    }
}
