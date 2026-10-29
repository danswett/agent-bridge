using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.Host.Shadow;

// One day of shadow comparison, as written to <profile>/shadow/shadow-<yyyy-MM-dd>.json. The day
// is the machine's local date. Kinds, hashed session ids, counts, enum values and times only.
public sealed class ShadowDay
{
    public int Schema { get; set; } = 1;
    public string Date { get; set; } = "";
    public string? TimeZone { get; set; }
    public int GraceSeconds { get; set; }
    public int IntervalSeconds { get; set; }
    // Samples taken, and those in which both sides could be read and so were compared.
    public int Samples { get; set; }
    public int Compared { get; set; }
    // How long both sides were compared, each compared sample counting the interval it was taken
    // at, so a run restarted with another --interval does not rescale the rest of the day.
    public long ComparedSeconds { get; set; }
    public DateTimeOffset? FirstSample { get; set; }
    public DateTimeOffset? LastSample { get; set; }
    public string? DaemonVersion { get; set; }
    public string? HostVersion { get; set; }
    public bool? HostDev { get; set; }
    public int DaemonSessionsMax { get; set; }
    public int HostSessionsMax { get; set; }
    public List<string> Sessions { get; set; } = [];
    // Each disagreement that lasted at least the grace: a mismatch, to be explained or fixed.
    public List<ShadowEpisode> Mismatches { get; set; } = [];
    // Mismatches past the cap on Mismatches, by kind: still counted, never silently dropped.
    public SortedDictionary<string, int> Overflow { get; set; } = new(StringComparer.Ordinal);
    // Disagreements that ended within the grace: timing skew between the two, by kind.
    public SortedDictionary<string, ShadowTransient> Transient { get; set; } = new(StringComparer.Ordinal);
    public List<string> NotCompared { get; set; } = [.. ShadowKinds.NotCompared];

    public string Verdict => Compared == 0 ? "no data" : Mismatches.Count > 0 || Overflow.Count > 0 ? "mismatches" : "clean";
}

public sealed class ShadowEpisode
{
    public string Kind { get; set; } = "";
    public string? Session { get; set; }
    public DateTimeOffset First { get; set; }
    public DateTimeOffset Last { get; set; }
    public long Seconds { get; set; }
    public int Samples { get; set; }
    // Still going when this file was last written. A host restart or the end of the day leaves it
    // set; the next day's file carries the same episode on with the same First.
    public bool Open { get; set; }
    public List<string> Details { get; set; } = [];
}

public sealed class ShadowTransient
{
    public int Count { get; set; }
    public long LongestSeconds { get; set; }
}

// Turns samples into episodes and episodes into the day's report. A disagreement becomes a
// mismatch only once it has lasted the grace: the daemon publishes on its own loop and the host
// within a second, so for a moment after every change the two differ by design.
public sealed class ShadowLedger
{
    public const int MaxMismatches = 1000;
    const int MaxDetails = 8;
    const int MaxSessions = 2000;
    // Either side being down, or the host not seeing the daemon, gets at least the three missed
    // beats agent-ha-bridge itself allows before calling a machine offline: both restart for
    // updates, and that is not a disagreement about what either shows.
    public static readonly TimeSpan MachineGrace = TimeSpan.FromSeconds(180);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    sealed class Episode(string kind, string? session, DateTimeOffset first)
    {
        public string Kind { get; } = kind;
        public string? Session { get; } = session;
        public DateTimeOffset First { get; } = first;
        public DateTimeOffset Last { get; set; } = first;
        public int Samples { get; set; }
        public List<string> Details { get; } = [];
        public ShadowEpisode? Entry { get; set; }
        public string? EntryDate { get; set; }
    }

    readonly string folder;
    readonly TimeSpan grace;
    readonly TimeSpan interval;
    readonly TimeProvider time;
    readonly Dictionary<(string Kind, string? Session), Episode> open = [];
    ShadowDay? day;

    public ShadowLedger(string folder, TimeSpan grace, TimeSpan interval, TimeProvider time)
    {
        this.folder = PrivateFolder.Ensure(folder, "the shadow report folder", "no report is written");
        this.grace = grace;
        this.interval = interval;
        this.time = time;
    }

    public string Folder => folder;

    // The day being recorded, once a sample has been.
    public ShadowDay? Today => day;

    public static string FileName(string date) => $"shadow-{date}.json";

    public static string LocalDate(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    TimeSpan GraceFor(string kind) => ShadowKinds.IsMachine(kind) && grace < MachineGrace ? MachineGrace : grace;

    // Records one sample and writes the day's file. Returns the mismatches that this sample made
    // (a disagreement that has just lasted its grace), for the log.
    public IReadOnlyList<ShadowEpisode> Record(ShadowComparison comparison, ShadowView daemon, ShadowView host)
    {
        var now = time.GetUtcNow();
        var date = LocalDate(now, time.LocalTimeZone);
        if (day is null || day.Date != date)
        {
            if (day is not null) { Save(day); }
            day = Load(date);
        }
        day.Samples++;
        if (comparison.Compared)
        {
            day.Compared++;
            day.ComparedSeconds += (long)interval.TotalSeconds;
        }
        day.FirstSample ??= now;
        day.LastSample = now;
        day.DaemonVersion = daemon.Version ?? day.DaemonVersion;
        day.HostVersion = host.Version ?? day.HostVersion;
        day.HostDev = host.Dev ?? day.HostDev;
        day.DaemonSessionsMax = Math.Max(day.DaemonSessionsMax, comparison.DaemonSessions);
        day.HostSessionsMax = Math.Max(day.HostSessionsMax, comparison.HostSessions);
        foreach (var s in comparison.SessionsSeen)
        {
            if (day.Sessions.Count < MaxSessions && !day.Sessions.Contains(s)) { day.Sessions.Add(s); }
        }

        var current = new Dictionary<(string, string?), ShadowDifference>();
        foreach (var d in comparison.Differences) { current.TryAdd((d.Kind, d.Session), d); }
        foreach (var (key, episode) in open.ToList())
        {
            if (current.ContainsKey(key)) { continue; }
            Close(episode);
            open.Remove(key);
        }
        var made = new List<ShadowEpisode>();
        foreach (var (key, d) in current)
        {
            if (!open.TryGetValue(key, out var episode)) { open[key] = episode = new Episode(d.Kind, d.Session, now); }
            episode.Last = now;
            episode.Samples++;
            if (episode.Details.Count < MaxDetails && !episode.Details.Contains(d.Detail)) { episode.Details.Add(d.Detail); }
            if (episode.Last - episode.First >= GraceFor(d.Kind) && Persist(episode, date) is { } fresh) { made.Add(fresh); }
        }
        Save(day);
        return made;
    }

    // Returns the entry when this is the moment the episode became a mismatch.
    ShadowEpisode? Persist(Episode e, string date)
    {
        var fresh = false;
        if (e.Entry is null || e.EntryDate != date)
        {
            fresh = e.Entry is null;
            // One that began before midnight and only lasted its grace after it belongs to the
            // day it began as well, or that day would read clean.
            if (fresh) { Backfill(e, date); }
            e.EntryDate = date;
            if (day!.Mismatches.Count >= MaxMismatches)
            {
                day.Overflow[e.Kind] = day.Overflow.GetValueOrDefault(e.Kind) + 1;
                e.Entry = new ShadowEpisode();
                return null;
            }
            e.Entry = new ShadowEpisode { Kind = e.Kind, Session = e.Session, First = e.First };
            day.Mismatches.Add(e.Entry);
        }
        e.Entry.Last = e.Last;
        e.Entry.Seconds = (long)(e.Last - e.First).TotalSeconds;
        e.Entry.Samples = e.Samples;
        e.Entry.Open = true;
        e.Entry.Details = [.. e.Details];
        return fresh ? e.Entry : null;
    }

    void Backfill(Episode e, string date)
    {
        var started = DateOnly.ParseExact(LocalDate(e.First, time.LocalTimeZone), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var today = DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        for (var d = started; d < today; d = d.AddDays(1))
        {
            var name = d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (Read(folder, name) is not { } earlier) { continue; }
            if (earlier.Mismatches.Count >= MaxMismatches) { earlier.Overflow[e.Kind] = earlier.Overflow.GetValueOrDefault(e.Kind) + 1; }
            else
            {
                earlier.Mismatches.Add(new ShadowEpisode
                {
                    Kind = e.Kind,
                    Session = e.Session,
                    First = e.First,
                    Last = e.Last,
                    Seconds = (long)(e.Last - e.First).TotalSeconds,
                    Samples = e.Samples,
                    // It went on past the end of that day.
                    Open = true,
                    Details = [.. e.Details],
                });
            }
            Save(earlier);
        }
    }

    void Close(Episode e)
    {
        if (e.Entry is not null) { e.Entry.Open = false; return; }
        var t = day!.Transient.TryGetValue(e.Kind, out var found) ? found : day.Transient[e.Kind] = new ShadowTransient();
        t.Count++;
        t.LongestSeconds = Math.Max(t.LongestSeconds, (long)(e.Last - e.First).TotalSeconds);
    }

    ShadowDay Load(string date)
    {
        var path = Path.Combine(folder, FileName(date));
        ShadowDay? loaded = null;
        if (File.Exists(path))
        {
            try { loaded = JsonSerializer.Deserialize<ShadowDay>(File.ReadAllText(path), Json); }
            catch (JsonException)
            {
                // Kept beside, never overwritten: it is the only record of that part of the day.
                File.Move(path, path + $".unreadable-{time.GetUtcNow().ToUnixTimeSeconds()}");
            }
        }
        // An episode a previous run left open ended when that run did; this one starts afresh.
        foreach (var m in loaded?.Mismatches ?? []) { m.Open = false; }
        loaded ??= new ShadowDay { Date = date };
        loaded.TimeZone = time.LocalTimeZone.Id;
        loaded.GraceSeconds = (int)grace.TotalSeconds;
        loaded.IntervalSeconds = (int)interval.TotalSeconds;
        loaded.NotCompared = [.. ShadowKinds.NotCompared];
        return loaded;
    }

    void Save(ShadowDay d)
    {
        var path = Path.Combine(folder, FileName(d.Date));
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(d, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static ShadowDay? Read(string folder, string date)
    {
        var path = Path.Combine(folder, FileName(date));
        if (!File.Exists(path)) { return null; }
        try { return JsonSerializer.Deserialize<ShadowDay>(File.ReadAllText(path), Json); }
        catch (JsonException) { return null; }
    }
}
