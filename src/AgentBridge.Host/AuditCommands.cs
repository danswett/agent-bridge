using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class AuditCommands
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    sealed record Row([property: JsonRequired] DateTimeOffset At, [property: JsonRequired] string Principal,
        [property: JsonRequired] string Action, [property: JsonRequired] string Outcome, string? Direction = null,
        string? Request = null, string[]? Chain = null, string? Target = null, string? Reason = null,
        string? DataSha256 = null, int? TextLength = null);

    public static int Run(IReadOnlyList<string> args, string profileRoot, TextWriter output, TextWriter error)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i += 2)
        {
            if (args[i] is not ("--profile" or "--action" or "--outcome" or "--since")
                || i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-')
                || !options.TryAdd(args[i], args[i + 1])) { return Usage(error); }
        }
        var profile = options.GetValueOrDefault("--profile", "default");
        if (!Identity.IsValidProfile(profile)) { return Usage(error); }
        DateTimeOffset? since = null;
        if (options.TryGetValue("--since", out var value))
        {
            if (!DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) { return Usage(error); }
            since = parsed;
        }

        try
        {
            var folder = Path.Combine(profileRoot, profile, AgentAuditLog.FolderName);
            // Directory.Exists also returns false for access failures: only genuinely missing data is empty.
            try { if ((File.GetAttributes(folder) & FileAttributes.Directory) == 0) { throw new IOException(); } }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return 0; }
            var files = Directory.GetFiles(folder, "*.jsonl");
            foreach (var file in files.Where(IsDaily).Order(StringComparer.Ordinal))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var terminated = true;
                if (stream.Length > 0) { stream.Seek(-1, SeekOrigin.End); terminated = stream.ReadByte() == '\n'; stream.Position = 0; }
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                var number = 0;
                while (reader.ReadLine() is { } raw)
                {
                    number++;
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(raw);
                        var jsonReader = new Utf8JsonReader(bytes, isFinalBlock: !reader.EndOfStream || terminated, state: default);
                        if (!JsonDocument.TryParseValue(ref jsonReader, out var document))
                        {
                            error.WriteLine($"audit: {Path.GetFileName(file)} line {number}: incomplete final record ignored.");
                            continue;
                        }
                        using var doc = document!;
                        if (jsonReader.Read() || jsonReader.BytesConsumed != bytes.Length) { throw new JsonException(); }
                        var row = Read(doc.RootElement);
                        if (row is null) { continue; }
                        if (since is { } start && row.At < start
                            || options.TryGetValue("--action", out var action) && row.Action != action
                            || options.TryGetValue("--outcome", out var outcome) && row.Outcome != outcome) { continue; }
                        output.WriteLine(JsonSerializer.Serialize(row with { At = TimeZoneInfo.ConvertTime(row.At, Pacific) }, Json));
                    }
                    catch (JsonException)
                    {
                        error.WriteLine($"audit: {Path.GetFileName(file)} line {number}: malformed record or unsupported version; report incomplete.");
                        return 1;
                    }
                }
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            error.WriteLine("audit: could not read the audit files; report incomplete.");
            return 1;
        }
    }

    static Row? Read(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
            if (root.TryGetProperty("v", out var version)
                && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)) { throw new JsonException(); }
            if (root.TryGetProperty("previousSha256", out var previous)
                && !root.EnumerateObject().Any(p => p.Name is "at" or "principal" or "action" or "outcome" or "direction"
                    or "request" or "chain" or "target" or "reason" or "dataSha256" or "textLength"))
            {
                if (!root.TryGetProperty("v", out _)
                    || previous.ValueKind != JsonValueKind.String
                    || previous.GetString() is not { } hash
                    || hash.Length != 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) { throw new JsonException(); }
                return null;
            }
            var row = root.Deserialize<Row>(Json) ?? throw new JsonException();
            if (string.IsNullOrEmpty(row.Principal) || string.IsNullOrEmpty(row.Action) || string.IsNullOrEmpty(row.Outcome)) { throw new JsonException(); }
            return row;
        }
        // JsonDocument defers decoding escaped Unicode until a property name or string is read.
        catch (InvalidOperationException e) { throw new JsonException("Invalid audit metadata.", e); }
    }

    static bool IsDaily(string path)
    {
        var name = Path.GetFileName(path);
        var prefix = name.StartsWith("agents-", StringComparison.Ordinal) ? 7
            : name.StartsWith("launches-", StringComparison.Ordinal) ? 9 : 0;
        return prefix != 0 && name.EndsWith(".jsonl", StringComparison.Ordinal)
            && DateTime.TryParseExact(name[prefix..^6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    static int Usage(TextWriter error)
    {
        error.WriteLine("usage: agent-bridge-host audit [--profile <name>] [--action <action>] [--outcome <outcome>] [--since <ISO time with offset>]");
        return 2;
    }
}
