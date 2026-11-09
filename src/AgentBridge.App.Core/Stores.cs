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

// When an update is ready on this machine (owner decision on #689, 6674007815; Product's names,
// 6674016765). Restart to update, for a machine a person uses: it waits for their press (#516).
// Install between turns, for a machine agents use: it installs by itself at the first moment no
// session there has a turn running or a question waiting (AutoUpdater).
[UnknownAs(RestartToUpdate)]
public enum UpdateWhenReady { RestartToUpdate, InstallBetweenTurns }

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
    // Check for and download updates in the background (AutoUpdater).
    public bool AutoUpdate { get; init; } = true;
    // When an update is ready, for this machine and profile, as the person set it; unset, it is
    // DefaultWhenReady, so a later default (Product's, for agent machines) reaches every machine
    // nobody set.
    public UpdateWhenReady? WhenUpdateReady { get; init; }
    public static UpdateWhenReady DefaultWhenReady => UpdateWhenReady.RestartToUpdate;
    [JsonIgnore]
    public UpdateWhenReady WhenReady => WhenUpdateReady ?? DefaultWhenReady;
    // Set just before an update is applied, and cleared by the new version's first start, which
    // announces it: the version updated from, and the new one's release notes.
    public string? UpdatedFrom { get; init; }
    public string? UpdateNotes { get; init; }
    // The window was open when that update was handed over (#1167): the next start opens it again,
    // with Updated or why it couldn't install there rather than a toast. Never set by an update
    // started while the window was closed, so an unattended machine never gets a window (#1159).
    public bool UpdateWindowOpen { get; init; }
    // Update.exe started for that update (AutoUpdater.HandedOver): an install was attempted. Unset,
    // a next start still on this version says nothing to the person (#1167).
    public bool UpdateHandedOver { get; init; }
    // The press from another device that update answers, if one asked for it (#51), and how the
    // last update asked for or installed ended, for the host to tell the account's devices.
    public string? UpdateRequest { get; init; }
    // Installs that failed in a row, and when the next may be tried by itself (UpdateBackoff):
    // kept because a failed update restarts the app.
    public int? UpdateFailures { get; init; }
    public DateTimeOffset? UpdateRetryAt { get; init; }
    // The release those failures were of: a different release pending is not held by them.
    public string? UpdateFailedRelease { get; init; }
    public UpdateResult? LastUpdate { get; init; }
    // The presses from other devices this app has taken on, the latest last: one the host still
    // lists after the app restarts (its report that it took them lost) is not carried out twice.
    public IReadOnlyList<string>? UpdateTaken { get; init; }
    // The presses from other devices being carried out (AutoUpdater): one the app had not finished
    // when it last stopped is reported as such by its next start.
    public string? UpdateInstallAsked { get; init; }
    public string? UpdateCheckAsked { get; init; }
    public string? Relay { get; init; }
    // Send feedback (#738): a staging override of the compiled-in recipient (key id and base64url
    // public key), used only when both are set; otherwise FeedbackSenderFactory's production key applies.
    public string? FeedbackKeyId { get; init; }
    public string? FeedbackPublicKey { get; init; }
    // Turning this off makes feedback unavailable, whatever else is set.
    public bool FeedbackUpload { get; init; } = true;
    // The installed app's first run turned on starting at sign-in (the host's logon task and
    // the app's Run entry); after that they are the person's to turn off and stay off.
    public bool SetUp { get; init; }
    // Set only by a first install (no settings file yet), cleared when Settings has opened on Get
    // started (#384). Never set for an upgrader, whose file already exists.
    public bool GuidePending { get; init; }

    // A missing file is a first run. An unreadable one is not: it belongs to someone who has used the app.
    public static bool IsFirstRun(string path) => !File.Exists(path);
    // Not now on the card for an agent installed after the bridge (#231): the version each agent
    // was dismissed at, by agent id. A different version is a new offer; the row's own Set up stays.
    public IReadOnlyDictionary<string, string>? AgentOffersDismissed { get; init; }

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
