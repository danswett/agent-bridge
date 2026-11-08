using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.App.Core.Feedback;

// Builds the allow-listed, bounded bundle. Nothing is read that is not named here: free text from
// a source (config values, journal, crash records, turns) is redacted and cut before it is written,
// and the bytes written are the bytes previewed and sent.
public sealed class FeedbackBundleSource : IFeedbackBundleSource
{
    // Config keys whose values are safe to show once redacted; every other key is only counted.
    public static readonly IReadOnlySet<string> ConfigAllowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "channel", "theme", "startOnLogin", "runInBackground", "notifications", "toasts", "language",
        "updateChannel", "autoUpdate", "reduceMotion", "relayEnabled",
    };

    const int MaxLineChars = 400;
    const int MaxCrashChars = 8000;
    const int MaxConfigValueChars = 100;

    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    readonly IFeedbackSources sources;
    readonly Func<DateTimeOffset> now;
    readonly TimeSpan timeout;

    public FeedbackBundleSource(IFeedbackSources sources, Func<DateTimeOffset>? now = null, TimeSpan? timeout = null)
    {
        this.sources = sources;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.timeout = timeout ?? FeedbackLimits.GatherTimeout;
    }

    public async Task<FeedbackGatherResult> GatherAsync(FeedbackRequest request, IProgress<FeedbackGatherStage>? progress, CancellationToken cancellationToken)
    {
        var note = request.Note ?? "";
        if (note.Length > FeedbackLimits.MaxNoteChars) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.NoteTooLong); }
        if (request.ScreenshotPng is { } shot)
        {
            if (shot.Length > FeedbackLimits.MaxScreenshotBytes) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.ScreenshotTooLarge); }
            if (shot.Length < PngSignature.Length || !shot.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.ScreenshotNotPng); }
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { return await BuildAsync(request, note, progress, limit.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Gathering feedback took too long.");
        }
    }

    async Task<FeedbackGatherResult> BuildAsync(FeedbackRequest request, string note, IProgress<FeedbackGatherStage>? progress, CancellationToken ct)
    {
        var items = new List<FeedbackItem>();
        var createdAt = now();
        var bundleId = RandomNumberGenerator.GetBytes(16);

        progress?.Report(FeedbackGatherStage.Versions);
        FeedbackEnvironment env;
        try { env = sources.Environment(); }
        catch (Exception) { env = new FeedbackEnvironment("unavailable", "unavailable", "unavailable", "unavailable", 0, []); }
        env = env with { HostVersions = env.HostVersions ?? [] };
        progress?.Report(FeedbackGatherStage.Agents);
        var agents = await Safe(sources.DetectAgentsAsync, ct);
        progress?.Report(FeedbackGatherStage.Config);
        var config = await Safe(sources.ConfigAsync, ct);
        progress?.Report(FeedbackGatherStage.Journal);
        var journal = await Safe(c => sources.JournalTailAsync(FeedbackLimits.MaxJournalLines, c), ct);
        progress?.Report(FeedbackGatherStage.Crashes);
        var crashes = await Safe(c => sources.CrashRecordsAsync(FeedbackLimits.MaxCrashRecords, c), ct);

        IReadOnlyList<FeedbackSessionTurn>? turns = null;
        if (request.SessionTurnsFor is { } session)
        {
            progress?.Report(FeedbackGatherStage.SessionTurns);
            turns = await Safe(c => sources.SessionTurnsAsync(session, FeedbackLimits.MaxSessionTurns, c), ct, fallback: null);
            if (turns is null) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.SessionNotFound); }
        }
        if (request.ScreenshotPng is not null) { progress?.Report(FeedbackGatherStage.Screenshot); }

        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", FeedbackLimits.Schema);
            w.WriteString("bundleId", Base64Url(bundleId));
            w.WriteString("createdAt", createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            w.WriteString("platform", "Windows");

            long mark = Mark(w, buffer);
            w.WriteStartObject("versions");
            w.WriteString("app", Clean(env.AppVersion, 128));
            w.WriteString("channel", Clean(env.Channel, 64));
            w.WriteString("os", Clean(env.OsDescription, 128));
            w.WriteString("architecture", Clean(env.Architecture, 32));
            w.WriteNumber("protocol", env.ProtocolVersion);
            w.WriteStartArray("hosts");
            foreach (var v in env.HostVersions.Select(h => Clean(h, 64)).Distinct().Order(StringComparer.Ordinal).Take(20)) { w.WriteStringValue(v); }
            w.WriteEndArray();
            w.WriteEndObject();
            items.Add(Item("versions", "App and host versions", $"App {Clean(env.AppVersion, 128)}, {Clean(env.Channel, 64)} channel, {Clean(env.OsDescription, 128)}", w, buffer, ref mark, false));

            w.WritePropertyName("agents");
            if (agents is null) { w.WriteStringValue("unavailable"); }
            else
            {
                w.WriteStartArray();
                foreach (var a in agents.OrderBy(a => a.Name, StringComparer.Ordinal).Take(20))
                {
                    w.WriteStartObject();
                    w.WriteString("name", Clean(a.Name, 64));
                    if (a.Version is not null) { w.WriteString("version", Clean(a.Version, 64)); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            items.Add(Item("agents", "Agents detected", agents is null ? "Unavailable" : $"{Math.Min(agents.Count, 20)} found, with versions", w, buffer, ref mark, false));

            w.WritePropertyName("config");
            if (config is null) { w.WriteStringValue("unavailable"); }
            else
            {
                var shown = config.Where(kv => ConfigAllowList.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                w.WriteStartObject();
                foreach (var (key, value) in shown) { w.WriteString(key, Clean(value, MaxConfigValueChars)); }
                w.WriteNumber("otherSettingsNotIncluded", config.Count - shown.Count);
                w.WriteEndObject();
            }
            items.Add(Item("config", "Settings summary", config is null ? "Unavailable" : "A short list of safe settings; the rest are not sent", w, buffer, ref mark, false));

            w.WritePropertyName("journal");
            if (journal is null) { w.WriteStringValue("unavailable"); }
            else
            {
                w.WriteStartArray();
                foreach (var line in journal.TakeLast(FeedbackLimits.MaxJournalLines)) { w.WriteStringValue(Clean(line, MaxLineChars)); }
                w.WriteEndArray();
            }
            items.Add(Item("journal", "Recent activity log", journal is null ? "Unavailable" : $"The last {Math.Min(journal.Count, FeedbackLimits.MaxJournalLines)} lines, redacted", w, buffer, ref mark, false));

            w.WritePropertyName("crashes");
            if (crashes is null) { w.WriteStringValue("unavailable"); }
            else
            {
                w.WriteStartArray();
                foreach (var crash in crashes.Take(FeedbackLimits.MaxCrashRecords)) { w.WriteStringValue(Clean(crash, MaxCrashChars)); }
                w.WriteEndArray();
            }
            items.Add(Item("crashes", "Crash records", crashes is null ? "Unavailable" : $"{Math.Min(crashes.Count, FeedbackLimits.MaxCrashRecords)} recent, redacted", w, buffer, ref mark, false));

            if (note.Trim().Length > 0)
            {
                var cleaned = Sanitize(FeedbackRedactor.Redact(note));
                w.WriteString("note", cleaned);
                items.Add(Item("note", "Your note", cleaned == note ? "Exactly what you typed" : "What you typed, with secrets removed", w, buffer, ref mark, true));
            }

            if (request.ScreenshotPng is { } png)
            {
                w.WriteString("screenshotPng", Convert.ToBase64String(png));
                items.Add(Item("screenshot", "Screenshot", $"{png.Length / 1024 + 1} KB image you attached", w, buffer, ref mark, true));
            }

            if (turns is not null)
            {
                w.WriteStartObject("sessionTurns");
                w.WriteStartArray("turns");
                foreach (var t in turns.TakeLast(FeedbackLimits.MaxSessionTurns))
                {
                    w.WriteStartObject();
                    w.WriteString("role", Clean(t.Role, 32));
                    w.WriteString("at", t.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                    w.WriteString("text", Clean(t.Text, FeedbackLimits.MaxTurnChars));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                items.Add(Item("sessionTurns", "Session turns", $"The last {Math.Min(turns.Count, FeedbackLimits.MaxSessionTurns)} turns of the session you chose, redacted", w, buffer, ref mark, true));
            }

            w.WriteEndObject();
        }

        progress?.Report(FeedbackGatherStage.Done);
        if (buffer.Length > FeedbackLimits.MaxBundleBytes) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.BundleTooLarge); }
        return new FeedbackGatherResult.Ready(new FeedbackBundle(buffer.ToArray(), bundleId, createdAt, items));
    }

    // A source that fails leaves its item out; the reason is not recorded since it could carry content.
    static async Task<T?> Safe<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct, T? fallback = default)
    {
        try { return await read(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return fallback; }
    }

    static string Clean(string? text, int max)
    {
        var redacted = Sanitize(FeedbackRedactor.Redact(text ?? ""));
        if (redacted.Length <= max) { return redacted; }
        // Don't cut between the halves of a surrogate pair; the JSON writer rejects a lone half.
        var cut = char.IsHighSurrogate(redacted[max - 1]) ? max - 1 : max;
        return redacted[..cut] + "…";
    }

    // Lone surrogates cannot be written as UTF-8 JSON.
    static string Sanitize(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c))
            {
                var chars = text.ToCharArray();
                for (var j = 0; j < chars.Length; j++)
                {
                    if (char.IsHighSurrogate(chars[j]) && j + 1 < chars.Length && char.IsLowSurrogate(chars[j + 1])) { j++; }
                    else if (char.IsSurrogate(chars[j])) { chars[j] = '\uFFFD'; }
                }
                return new string(chars);
            }
        }
        return text;
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Sizes an item by the bytes written since the last one; flushing keeps the count exact.
    static long Mark(Utf8JsonWriter w, MemoryStream buffer) { w.Flush(); return buffer.Length; }

    static FeedbackItem Item(string id, string label, string summary, Utf8JsonWriter w, MemoryStream buffer, ref long mark, bool optional)
    {
        w.Flush();
        var bytes = buffer.Length - mark;
        mark = buffer.Length;
        return new FeedbackItem(id, label, summary, bytes, optional);
    }
}
