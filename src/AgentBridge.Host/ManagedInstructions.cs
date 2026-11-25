using System.Text;
using System.Text.RegularExpressions;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host;

// Plans from one byte snapshot; DurableFile refuses a save that lands before commit.
internal sealed record ManagedInstructions(string FilePath, byte[]? Original, byte[]? Updated)
{
    const string Start = "<!-- agent-bridge-owner";
    const string End = "<!-- /agent-bridge-owner -->";
    static readonly Regex Header = new(@"\A<!-- agent-bridge-owner:([0-9a-f]{64});created=([01]);separator=([01]) -->\z",
        RegexOptions.CultureInvariant);
    // One source with what Claude and Codex now get at SessionStart (AgentBridge.Hooks.SessionGuidance).
    const string Guidance = AgentBridge.Hooks.SessionGuidance.Text;

    public static ManagedInstructions Plan(string path, string profile, bool removing)
    {
        if (!File.Exists(path) && new FileInfo(path).LinkTarget is not null)
        {
            throw Refused(path, "has an unresolved instruction-file link");
        }
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (DurableFile.HardLinked(path)) { throw Refused(path, "has another hard link and cannot be safely rewritten"); }
        var (text, encoding, preamble) = Decode(path, original ?? []);
        var normalized = StandaloneHooks.Normal(profile);
        if (OperatingSystem.IsWindows()) { normalized = normalized.ToUpperInvariant(); }
        var owner = MigrationRecord.Hash(Encoding.UTF8.GetBytes(normalized));
        RefuseLegacy(path, text);
        var start = text.IndexOf(Start, StringComparison.Ordinal);
        var end = text.IndexOf("<!-- /agent-bridge-owner", StringComparison.Ordinal);
        var created = original is null;
        var separator = text.Length > 0;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.Contains('\n') ? "\n" : text.Contains('\r') ? "\r" : Environment.NewLine;
        var stop = 0;
        if (start >= 0 || end >= 0)
        {
            if (start < 0 || end < start || text.IndexOf(Start, start + Start.Length, StringComparison.Ordinal) >= 0
                || text.IndexOf("<!-- /agent-bridge-owner", end + 1, StringComparison.Ordinal) >= 0)
            {
                throw Refused(path, "has incomplete or multiple owned instruction blocks");
            }
            var headerEnd = text.IndexOf("-->", start, StringComparison.Ordinal);
            if (headerEnd < 0 || headerEnd >= end) { throw Refused(path, "has an incomplete instruction owner"); }
            var match = Header.Match(text[start..(headerEnd + 3)]);
            if (!match.Success) { throw Refused(path, "has a malformed instruction owner"); }
            if (match.Groups[1].Value != owner) { throw Refused(path, "has instructions owned by another installation"); }
            newline = NewlineAt(text, headerEnd + 3);
            if (newline.Length == 0 || (start > 0 && text[start - 1] is not ('\n' or '\r'))
                || !text.AsSpan(end).StartsWith(End, StringComparison.Ordinal))
            {
                throw Refused(path, "has damaged instruction block boundaries");
            }
            stop = end + End.Length;
            if (NewlineAt(text, stop) != newline) { throw Refused(path, "has an incomplete instruction block ending"); }
            stop += newline.Length;
            created = match.Groups[2].Value == "1";
            separator = match.Groups[3].Value == "1";
            if (separator && (start < newline.Length || text[(start - newline.Length)..start] != newline))
            {
                throw Refused(path, "has a damaged instruction block separator");
            }
        }

        string wanted;
        if (removing)
        {
            if (start < 0) { return new(path, original, original); }
            wanted = text[..(start - (separator ? newline.Length : 0))] + text[stop..];
            // A link belongs to the person even when its target held only our block.
            if (created && wanted.Length == 0 && new FileInfo(path).LinkTarget is null)
            {
                return new(path, original, null);
            }
        }
        else
        {
            var block = $"<!-- agent-bridge-owner:{owner};created={(created ? 1 : 0)};separator={(separator ? 1 : 0)} -->"
                + newline + Guidance.ReplaceLineEndings(newline) + newline + End + newline;
            wanted = start >= 0 ? text[..start] + block + text[stop..]
                : text + (separator ? newline : "") + block;
        }
        return new(path, original, [.. preamble, .. encoding.GetBytes(wanted)]);
    }

    // Whether applying this plan would change the person's file.
    public bool Changes => !(Original is null && Updated is null || Original is not null && Updated is not null && Original.AsSpan().SequenceEqual(Updated));

    // A removal also keeps removalBackup, the file's exact bytes just before this removal: the
    // first-original backup is only the file as it was at the first Set up, which the person may
    // have edited since, so it cannot undo this one.
    public void Apply(string backup, string? removalBackup = null)
    {
        if (!Changes) { return; }
        if (Original is not null && removalBackup is not null) { DurableFile.Replace(removalBackup, Original, null); }
        if (Original is not null && !File.Exists(backup)) { DurableFile.Replace(backup, Original, null); }
        var expected = Original is null ? null : MigrationRecord.Hash(Original);
        if (Updated is null) { DurableFile.Remove(FilePath, expected); }
        else { DurableFile.Replace(FilePath, Updated, expected); }
    }

    public static void CheckLegacy(string path)
    {
        if (File.Exists(path)) { RefuseLegacy(path, Decode(path, File.ReadAllBytes(path)).Text); }
    }

    static void RefuseLegacy(string path, string text)
    {
        if (text.Contains("<!-- agent-ha-bridge-owner", StringComparison.Ordinal)
            || text.Contains("<!-- /agent-ha-bridge", StringComparison.Ordinal))
        {
            throw Refused(path, "contains legacy guidance; inspect it for migration");
        }
    }

    static string NewlineAt(string text, int at) => text.AsSpan(at).StartsWith("\r\n", StringComparison.Ordinal)
        ? "\r\n" : at < text.Length && text[at] is '\n' or '\r' ? text[at].ToString() : "";

    static (string Text, Encoding Encoding, byte[] Preamble) Decode(string path, byte[] bytes)
    {
        Encoding encoding = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) ? new UTF32Encoding(false, true, true)
            : bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) ? new UTF32Encoding(true, true, true)
            : bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? new UnicodeEncoding(false, true, true)
            : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? new UnicodeEncoding(true, true, true)
            : new UTF8Encoding(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), true);
        var preamble = encoding.GetPreamble();
        try { return (encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding, preamble); }
        catch (DecoderFallbackException) { throw Refused(path, "has an unsupported or damaged text encoding"); }
    }

    static InvalidDataException Refused(string path, string why) => new($"{path} {why}; it was left alone");
}
