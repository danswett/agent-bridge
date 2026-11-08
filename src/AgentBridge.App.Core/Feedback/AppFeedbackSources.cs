using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core.Feedback;

// What the real "Send feedback" reads (#738 S3c): the app's own files and the sources it already
// has (the host's agent report, session.history). Every read is read-only and keeps nothing; a
// missing, locked or malformed file leaves that source empty rather than failing the bundle.
public sealed partial class AppFeedbackSources : IFeedbackSources
{
    // Fail closed rather than take an unchecked tail of a log that exceeds this bound.
    const int MaxLogFileChars = 4 * 1024 * 1024;

    readonly Func<FeedbackEnvironment> environment;
    readonly string appFolder;
    readonly string settingsFile;
    readonly Func<CancellationToken, Task<AgentReadinessReport?>> agents;
    readonly Func<string, int, CancellationToken, Task<HistoryResult?>> history;

    // appFolder holds app.log (and app.log.1) and crash.log, as AppLog and CrashLog write them.
    // agents: this machine's host report (null when the host cannot say). history: session.history
    // for one session, newest page; null when there is no host to ask.
    public AppFeedbackSources(
        Func<FeedbackEnvironment> environment,
        string appFolder,
        string settingsFile,
        Func<CancellationToken, Task<AgentReadinessReport?>> agents,
        Func<string, int, CancellationToken, Task<HistoryResult?>> history)
    {
        this.environment = environment;
        this.appFolder = appFolder;
        this.settingsFile = settingsFile;
        this.agents = agents;
        this.history = history;
    }

    // The parts that need no host: what the OS and the protocol say. channel is the update channel
    // the app follows, hostVersions the versions of the hosts it is connected to.
    public static FeedbackEnvironment Describe(string appVersion, string channel, IReadOnlyList<string> hostVersions) =>
        new(appVersion, channel, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), ProtocolJson.Version, hostVersions);

    public FeedbackEnvironment Environment() => environment();

    public async Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await agents(cancellationToken);
            if (report is null) { return []; }
            return [.. report.Agents
                .Where(a => a.Executable is not null)
                .Select(a => new FeedbackAgent(a.Label, string.IsNullOrWhiteSpace(a.Version) ? null : a.Version))];
        }
        catch (Exception e) when (e is not OperationCanceledException) { return []; }
    }

    public Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var text = ReadAll(settingsFile);
            if (text is not null)
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        {
                            found[p.Name] = p.Value.ToString();
                        }
                    }
                    // The app stores a flag; the bundle's allow-list names the channel.
                    if (!found.ContainsKey("updateChannel"))
                    {
                        found["updateChannel"] = !doc.RootElement.TryGetProperty("prereleases", out var pre) || pre.ValueKind != JsonValueKind.False ? "prerelease" : "stable";
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { found.Clear(); }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(found);
    }

    public Task<IReadOnlyList<string>> JournalTailAsync(int maxLines, CancellationToken cancellationToken)
    {
        if (maxLines <= 0) { return Task.FromResult<IReadOnlyList<string>>([]); }
        // The rotated file is older, so it comes first.
        var lines = new List<string>();
        foreach (var name in new[] { "app.log.1", "app.log" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, tooLarge) = ReadBoundedLog(Path.Combine(appFolder, name));
            if (tooLarge || (text is not null && (IncompleteLine(text) || FeedbackRedactor.MentionsSensitiveField(text))))
            {
                return Task.FromResult<IReadOnlyList<string>>([FeedbackBundleSource.JournalOmitted]);
            }
            if (text is not null) { lines.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))); }
        }
        return Task.FromResult<IReadOnlyList<string>>(lines.Count > maxLines ? [FeedbackBundleSource.JournalOmitted] : lines);
    }

    public Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken cancellationToken)
    {
        if (max <= 0) { return Task.FromResult<IReadOnlyList<string>>([]); }
        var (text, tooLarge) = ReadBoundedLog(Path.Combine(appFolder, "crash.log"));
        if (tooLarge || (text is not null && (IncompleteLine(text) || FeedbackRedactor.MentionsSensitiveField(text))))
        {
            return Task.FromResult<IReadOnlyList<string>>([FeedbackBundleSource.CrashOmitted]);
        }
        if (text is null) { return Task.FromResult<IReadOnlyList<string>>([]); }
        // CrashLog writes "<ISO time> <exception text>\n"; the text spans lines, so a record
        // begins at a line that starts with a timestamp.
        var records = new List<StringBuilder>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (RecordStart().IsMatch(l)) { records.Add(new StringBuilder(l)); }
            else if (records.Count > 0) { records[^1].Append('\n').Append(l); }
            else if (!string.IsNullOrWhiteSpace(l)) { return Task.FromResult<IReadOnlyList<string>>([FeedbackBundleSource.CrashOmitted]); }
        }
        return Task.FromResult<IReadOnlyList<string>>(records.Count > max
            ? [FeedbackBundleSource.CrashOmitted]
            : [.. records.Select(r => r.ToString().TrimEnd()).Reverse()]);
    }

    public async Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string sessionId, int max, CancellationToken cancellationToken)
    {
        // A failure to read propagates: the gatherer tells "the host could not say" (SessionUnavailable)
        // from "no such session" (null).
        var page = await history(sessionId, Math.Clamp(max, 1, HistoryLimits.MaxTurns), cancellationToken)
            ?? throw new InvalidOperationException("No host to ask for the session's history.");
        if (page.Missing == true) { return null; }
        if (page.Unavailable is not null || page.Stale == true || page.Turns is null) { throw new InvalidOperationException("The host has no history for this session."); }

        if (page.Partial == true || page.Before is not null || page.Start is null || page.Start.Limited == true || page.Turns.Count > max
            || page.Turns.Any(t => t.Unreadable == true || t.Truncated == true || t.PartsMore is > 0
                || t.Prompt?.Truncated == true || t.Parts.Any(p => p.Truncated == true || p.Kind == HistoryPartKind.Unknown
                    || (p.Kind == HistoryPartKind.Response && p.Text is null)))
            || FeedbackRedactor.MentionsSensitiveField(JsonSerializer.Serialize(page, ProtocolJson.Options)))
        {
            return [new FeedbackSessionTurn("user", FeedbackBundleSource.TurnsOmitted, DateTimeOffset.UnixEpoch)];
        }

        var turns = new List<FeedbackSessionTurn>();
        foreach (var t in page.Turns)
        {
            if (t.Prompt is { } p) { turns.Add(new FeedbackSessionTurn(p.From is null or UserMessageFrom.Person ? "user" : "other", p.Text, t.At)); }
            foreach (var part in t.Parts)
            {
                if (part.Kind == HistoryPartKind.Response && part.Text is { } text) { turns.Add(new FeedbackSessionTurn("assistant", text, part.At)); }
            }
        }
        if (turns.Count > max)
        {
            return [new FeedbackSessionTurn("user", FeedbackBundleSource.TurnsOmitted, DateTimeOffset.UnixEpoch)];
        }
        return turns;
    }

    static bool IncompleteLine(string text) => text.Length > 0 && !text.EndsWith('\n');

    static (string? Text, bool TooLarge) ReadBoundedLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            var chars = new char[MaxLogFileChars + 1];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            return count > MaxLogFileChars ? (null, true) : (new string(chars, 0, count), false);
        }
        catch (FileNotFoundException) { return (null, false); }
        catch (DirectoryNotFoundException) { return (null, false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { return (null, true); }
    }

    // Shared with the app, which appends while this reads.
    static string? ReadAll(string path)
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), true);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d")]
    private static partial Regex RecordStart();
}
