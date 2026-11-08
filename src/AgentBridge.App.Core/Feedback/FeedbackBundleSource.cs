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

    internal const string JournalOmitted = "Recent activity log left out: it may contain passwords or tokens, or could not be checked completely.";
    internal const string CrashOmitted = "Crash records left out: they may contain passwords or tokens, or could not be checked completely.";
    internal const string TextOmitted = "Text left out: it may contain passwords or tokens.";
    internal const string NoteOmitted = "Your note was left out: it may contain passwords or tokens. Reword it and preview again.";
    internal const string TurnsOmitted = "Session turns left out: they may contain passwords or tokens, or could not be checked completely. Deselect them, or choose another session.";

    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    readonly IFeedbackSources sources;
    readonly Func<DateTimeOffset> now;
    readonly TimeSpan timeout;
    readonly IReadOnlyList<string> privateNames;

    public FeedbackBundleSource(IFeedbackSources sources, Func<DateTimeOffset>? now = null, TimeSpan? timeout = null, IReadOnlyList<string>? privateNames = null)
    {
        this.privateNames = privateNames ?? [System.Environment.UserName, System.Environment.MachineName];
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
        try { env = await Task.Run(sources.Environment, ct).WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
            try { turns = await sources.SessionTurnsAsync(session, FeedbackLimits.MaxSessionTurns, ct).WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { turns = [new FeedbackSessionTurn("user", TurnsOmitted, DateTimeOffset.UnixEpoch)]; }
            if (turns is null) { return new FeedbackGatherResult.Refused(FeedbackRefusalReason.SessionNotFound); }
        }
        if (request.ScreenshotPng is not null) { progress?.Report(FeedbackGatherStage.Screenshot); }

        var metadataOmitted = false;
        string CleanMetadata(string? value, int max)
        {
            var cleaned = Clean(value, max);
            metadataOmitted |= cleaned == TextOmitted;
            return cleaned;
        }
        string MetadataSummary(string summary) => metadataOmitted ? summary + " Some values were left out; check the preview." : summary;

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
            var appVersion = CleanMetadata(env.AppVersion, 128);
            var channel = CleanMetadata(env.Channel, 64);
            var os = CleanMetadata(env.OsDescription, 128);
            w.WriteString("app", appVersion);
            w.WriteString("channel", channel);
            w.WriteString("os", os);
            w.WriteString("architecture", CleanMetadata(env.Architecture, 32));
            w.WriteNumber("protocol", env.ProtocolVersion);
            w.WriteStartArray("hosts");
            foreach (var v in env.HostVersions.Select(h => CleanMetadata(h, 64)).Distinct().Order(StringComparer.Ordinal).Take(20)) { w.WriteStringValue(v); }
            w.WriteEndArray();
            w.WriteEndObject();
            items.Add(Item("versions", "App and host versions", MetadataSummary($"App {appVersion}, {channel} channel, {os}"), w, buffer, ref mark, false));

            metadataOmitted = false;
            w.WritePropertyName("agents");
            if (agents is null) { w.WriteStringValue("unavailable"); }
            else
            {
                w.WriteStartArray();
                foreach (var a in agents.OrderBy(a => a.Name, StringComparer.Ordinal).Take(20))
                {
                    w.WriteStartObject();
                    w.WriteString("name", CleanMetadata(a.Name, 64));
                    if (a.Version is not null) { w.WriteString("version", CleanMetadata(a.Version, 64)); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            items.Add(Item("agents", "Agents detected", MetadataSummary(agents is null ? "Unavailable" : $"{Math.Min(agents.Count, 20)} found, with versions"), w, buffer, ref mark, false));

            metadataOmitted = false;
            w.WritePropertyName("config");
            if (config is null) { w.WriteStringValue("unavailable"); }
            else
            {
                var shown = config.Where(kv => ConfigAllowList.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
                w.WriteStartObject();
                foreach (var (key, value) in shown) { w.WriteString(key, CleanMetadata(value, MaxConfigValueChars)); }
                w.WriteNumber("otherSettingsNotIncluded", config.Count - shown.Count);
                w.WriteEndObject();
            }
            items.Add(Item("config", "Settings summary", MetadataSummary(config is null ? "Unavailable" : "A short list of safe settings; the rest are not sent"), w, buffer, ref mark, false));

            w.WritePropertyName("journal");
            var journalLines = journal?.TakeLast(FeedbackLimits.MaxJournalLines).ToArray();
            var journalOmitted = journal is not null && (journal.Count > FeedbackLimits.MaxJournalLines
                || journal.Any(line => line == JournalOmitted || line.Length > MaxLineChars || FeedbackRedactor.MentionsSensitiveField(line))
                || FeedbackRedactor.MentionsSensitiveField(string.Concat(journal)));
            if (journal is null) { w.WriteStringValue("unavailable"); }
            else if (journalOmitted) { w.WriteStringValue(JournalOmitted); }
            else
            {
                w.WriteStartArray();
                foreach (var line in journalLines!) { w.WriteStringValue(CleanLog(line, MaxLineChars)); }
                w.WriteEndArray();
            }
            items.Add(Item("journal", "Recent activity log", journal is null ? "Unavailable" : journalOmitted ? JournalOmitted : $"The last {journalLines!.Length} lines, redacted", w, buffer, ref mark, false));

            w.WritePropertyName("crashes");
            var crashRecords = crashes?.Take(FeedbackLimits.MaxCrashRecords).ToArray();
            var crashesOmitted = crashes is not null && (crashes.Count > FeedbackLimits.MaxCrashRecords
                || crashes.Any(crash => crash == CrashOmitted || crash.Length > MaxCrashChars || FeedbackRedactor.MentionsSensitiveField(crash))
                || FeedbackRedactor.MentionsSensitiveField(string.Concat(crashes)));
            if (crashes is null) { w.WriteStringValue("unavailable"); }
            else if (crashesOmitted) { w.WriteStringValue(CrashOmitted); }
            else
            {
                w.WriteStartArray();
                foreach (var crash in crashRecords!) { w.WriteStringValue(CleanLog(crash, MaxCrashChars)); }
                w.WriteEndArray();
            }
            items.Add(Item("crashes", "Crash records", crashes is null ? "Unavailable" : crashesOmitted
                ? CrashOmitted
                : $"{crashRecords!.Length} recent, redacted", w, buffer, ref mark, false));

            if (note.Trim().Length > 0)
            {
                var noteOmitted = FeedbackRedactor.MentionsSensitiveField(note);
                var cleaned = noteOmitted ? NoteOmitted : Sanitize(FeedbackRedactor.Redact(note, privateNames));
                w.WriteString("note", cleaned);
                items.Add(Item("note", "Your note", noteOmitted ? NoteOmitted : cleaned == note ? "Exactly what you typed" : "What you typed, with redactions; check the preview", w, buffer, ref mark, true));
            }

            if (request.ScreenshotPng is { } png)
            {
                w.WriteString("screenshotPng", Convert.ToBase64String(png));
                items.Add(Item("screenshot", "Screenshot", $"{png.Length / 1024 + 1} KB image you attached", w, buffer, ref mark, true));
            }

            if (turns is not null)
            {
                var turnsOmitted = turns.Count > FeedbackLimits.MaxSessionTurns || turns.Any(t =>
                    t.Text == TurnsOmitted || t.Text.Length > FeedbackLimits.MaxTurnChars || t.Role.Length > 32
                    || FeedbackRedactor.MentionsSensitiveField(t.Text) || FeedbackRedactor.MentionsSensitiveField(t.Role))
                    || FeedbackRedactor.MentionsSensitiveField(string.Concat(turns.Select(t => t.Text)));
                if (turnsOmitted) { w.WriteString("sessionTurns", TurnsOmitted); }
                else
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
                }
                items.Add(Item("sessionTurns", "Session turns", turnsOmitted ? TurnsOmitted : $"The last {Math.Min(turns.Count, FeedbackLimits.MaxSessionTurns)} turns of the session you chose, redacted", w, buffer, ref mark, true));
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
        try { return await read(ct).WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return fallback; }
    }

    string CleanLog(string text, int max) => Clean(FeedbackRedactor.RedactLogLines(text, privateNames), max);

    internal string Clean(string? text, int max)
    {
        if (FeedbackRedactor.MentionsSensitiveField((text ?? "").Replace(FeedbackRedactor.EscapedMark, "", StringComparison.Ordinal))) { return TextOmitted; }
        var redacted = Sanitize(FeedbackRedactor.Redact(text ?? "", privateNames));
        if (redacted.Length <= max) { return redacted; }
        // Don't cut between the halves of a surrogate pair; the JSON writer rejects a lone half.
        var cut = char.IsHighSurrogate(redacted[max - 1]) ? max - 1 : max;
        var kept = redacted[..cut];
        // The cut must not leave half a placeholder or a key with its value gone: the bundle has to
        // pass the same secret check the sender runs, so back up to an earlier boundary until it does.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var fragment = DanglingPlaceholder(kept);
            if (fragment >= 0) { kept = kept[..fragment]; }
            if (!FeedbackRedactor.ContainsSecret(kept + "…")) { return kept + "…"; }
            var boundary = kept.TrimEnd().LastIndexOfAny([' ', '\t', '\r', '\n']);
            if (boundary < 0) { break; }
            kept = kept[..boundary];
        }
        return TruncatedMark;
    }

    const string TruncatedMark = "[redacted: truncated]";

    // Start of a trailing piece of a redaction placeholder ([redacted], <user>, <host>) that the cut
    // interrupted, or -1.
    static int DanglingPlaceholder(string text)
    {
        foreach (var placeholder in new[] { FeedbackRedactor.Mark, FeedbackRedactor.EscapedMark, "<user>", "<host>" })
        {
            var open = placeholder[0];
            var start = text.LastIndexOf(open);
            if (start >= 0 && text.Length - start < placeholder.Length && placeholder.StartsWith(text[start..], StringComparison.Ordinal)) { return start; }
        }
        return -1;
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
