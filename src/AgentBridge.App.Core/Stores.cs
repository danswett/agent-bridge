using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Whether each session's card is folded, kept across restarts of the app (pwa/js/ui/
// fold-store.js). It also remembers which question last opened a card: a new question opens a
// folded card once, so it is never folded away unseen, but folding it again while the same
// question waits is respected.
public sealed class FoldStore
{
    readonly string? path;
    readonly Dictionary<string, Entry> entries;

    sealed record Entry(bool Folded, string? Question);

    public FoldStore(string? path)
    {
        this.path = path;
        entries = Load(path);
    }

    static Dictionary<string, Entry> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? []; }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }

    public bool IsFolded(string id) => entries.GetValueOrDefault(id)?.Folded == true;

    public void SetFolded(string id, bool folded)
    {
        Write(id, (entries.GetValueOrDefault(id) ?? new Entry(false, null)) with { Folded = folded });
    }

    // Called with the pending question's id (or null). True if the card was folded and has
    // just been opened for a new question.
    public bool NoteQuestion(string id, string? question)
    {
        var e = entries.GetValueOrDefault(id);
        if (question is null || e?.Question == question) { return false; }
        Write(id, new Entry(false, question));
        return e?.Folded == true;
    }

    // Sessions that are gone are forgotten, so the file does not grow forever.
    public void Keep(IEnumerable<string> live)
    {
        var keep = live.ToHashSet(StringComparer.Ordinal);
        var gone = entries.Keys.Where(k => !keep.Contains(k)).ToList();
        if (gone.Count == 0) { return; }
        foreach (var k in gone) { entries.Remove(k); }
        Save();
    }

    void Write(string id, Entry e)
    {
        if (!e.Folded && e.Question is null) { entries.Remove(id); } else { entries[id] = e; }
        Save();
    }

    void Save()
    {
        if (path is null) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        // A nicety: the card still works without it.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

// The app's own preferences, beside the host's data: what the app shows and does, never what
// the host may do (that is the host's config.json, changed through host.settings).
public sealed record AppPreferences
{
    // The old per-machine Detail toggle: reasoning and the tool history on cards.
    public bool ShowDetail { get; init; } = true;
    public bool Toasts { get; init; } = true;
    public bool FinishedToasts { get; init; } = true;
    // Include pre-releases when checking for updates.
    public bool Prereleases { get; init; } = true;
    // Install updates without being asked (AutoUpdater).
    public bool AutoUpdate { get; init; } = true;
    // Set just before an update is applied, and cleared by the new version's first start, which
    // announces it: the version updated from, and the new one's release notes.
    public string? UpdatedFrom { get; init; }
    public string? UpdateNotes { get; init; }
    // The press from another device that update answers, if one asked for it (#51), and how the
    // last update asked for or installed ended, for the host to tell the account's devices.
    public string? UpdateRequest { get; init; }
    public UpdateResult? LastUpdate { get; init; }
    // The presses from other devices this app has taken on, the latest last: one the host still
    // lists after the app restarts (its report that it took them lost) is not carried out twice.
    public IReadOnlyList<string>? UpdateTaken { get; init; }
    public string? Relay { get; init; }
    // The installed app's first run turned on starting at sign-in (the host's logon task and
    // the app's Run entry); after that they are the person's to turn off and stay off.
    public bool SetUp { get; init; }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // LastUpdate's outcome as the protocol's string, read back tolerantly.
        Converters = { new TolerantEnumConverterFactory() },
    };

    public static AppPreferences Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
