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
    // A log can be a megabyte (AppLog rotates at 1,000,000); the newest lines are all that is wanted.
    const int TailBytes = 256 * 1024;

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
            var text = ReadTail(Path.Combine(appFolder, name));
            if (text is not null) { lines.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))); }
        }
        return Task.FromResult<IReadOnlyList<string>>([.. lines.Skip(Math.Max(0, lines.Count - maxLines))]);
    }

    public Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken cancellationToken)
    {
        if (max <= 0) { return Task.FromResult<IReadOnlyList<string>>([]); }
        var text = ReadTail(Path.Combine(appFolder, "crash.log"));
        if (text is null) { return Task.FromResult<IReadOnlyList<string>>([]); }
        // CrashLog writes "<ISO time> <exception text>\n"; the text spans lines, so a record
        // begins at a line that starts with a timestamp. A cut at the start of the tail is not one.
        var records = new List<StringBuilder>();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (RecordStart().IsMatch(l)) { records.Add(new StringBuilder(l)); }
            else if (records.Count > 0) { records[^1].Append('\n').Append(l); }
        }
        return Task.FromResult<IReadOnlyList<string>>([.. records.Select(r => r.ToString().TrimEnd()).Reverse().Take(max)]);
    }

    public async Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string sessionId, int max, CancellationToken cancellationToken)
    {
        // A failure to read propagates: the gatherer tells "the host could not say" (SessionUnavailable)
        // from "no such session" (null).
        var page = await history(sessionId, Math.Clamp(max, 1, HistoryLimits.MaxTurns), cancellationToken)
            ?? throw new InvalidOperationException("No host to ask for the session's history.");
        if (page.Missing == true) { return null; }
        if (page.Unavailable is not null || page.Stale == true || page.Turns is null) { throw new InvalidOperationException("The host has no history for this session."); }

        var turns = new List<FeedbackSessionTurn>();
        foreach (var t in page.Turns)
        {
            if (t.Unreadable == true) { continue; }
            if (t.Prompt is { } p) { turns.Add(new FeedbackSessionTurn(p.From is null or UserMessageFrom.Person ? "user" : "other", p.Text, t.At)); }
            foreach (var part in t.Parts)
            {
                if (part.Kind == HistoryPartKind.Response && part.Text is { } text) { turns.Add(new FeedbackSessionTurn("assistant", text, part.At)); }
            }
        }
        return [.. turns.Skip(Math.Max(0, turns.Count - max))];
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

    // The last TailBytes of a file, from a line start. Invalid bytes read as replacement characters.
    static string? ReadTail(string path)
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) { break; }
                read += n;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (start > 0)
            {
                var nl = text.IndexOf('\n');
                text = nl < 0 ? "" : text[(nl + 1)..];
            }
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d")]
    private static partial Regex RecordStart();
}
