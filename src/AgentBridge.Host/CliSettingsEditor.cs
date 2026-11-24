using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host;

// The ONE editor for a CLI's own settings file (docs/design/beta-messaging.md section 6; owner decision, Nov 23 about 23:05 PT, "Yes, with backup + notice").
// The rules it carries, for the hook install and for the MCP server registration to come: read what the person wrote (comments, trailing commas, a byte order
// mark), keep the FIRST original byte for byte before the first change, add only this product's own entries, say what is changed and how to undo it, write
// durably, and on removal give the original back when only our entries differed. It decides nothing about WHICH entries belong to whom: the caller does.
internal static class CliSettingsEditor
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static JsonObject Read(string path) => Parse(path, File.ReadAllBytes(path));

    public static JsonObject Parse(string path, byte[] bytes) => JsonNode.Parse(Registrations.Decode(bytes),
        documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
        ?? throw new InvalidDataException($"{path} is not a JSON object; it was left alone");

    public static byte[] Bytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString(Pretty) + Environment.NewLine);

    // The first original, once: a file that is not there yet has nothing to keep, and a backup already taken is never overwritten.
    public static void BackupOnce(string backup, string live)
    {
        if (File.Exists(live) && !File.Exists(backup)) { DurableFile.Replace(backup, File.ReadAllBytes(live), null); }
    }

    // The first original settings, byte for byte (comments and formatting included), when the settings without our entries still parse to exactly what they held;
    // otherwise null, and removal rewrites the current settings without our entries. A missing backup is not proof that setup created the file, so the file is
    // never deleted. Only a backup that is actually absent is skipped quietly: File.Exists is also false for a directory or a path it may not inspect, which must
    // warn like any other unreadable backup.
    public static byte[]? Original(string backup, JsonObject remaining, TextWriter output, string what)
    {
        try
        {
            var original = File.ReadAllBytes(backup);
            return JsonNode.DeepEquals(Parse(backup, original), remaining) ? original : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        // ArgumentException: duplicate property names parse but throw when DeepEquals reads them.
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Warning: {backup} is unreadable ({e.Message}); the original settings cannot be restored, so only the owned {what} are removed from the current settings.");
            return null;
        }
    }

    // What the person is told before and while a settings file is changed: the path, what is added, that nothing else is touched, where the original is kept, the way back.
    public static string Notice(bool dryRun, string path, string adds, string backup) =>
        $"{(dryRun ? "Would change" : "Changing")} {path}: adds {adds} and nothing else; your other settings are not touched. The original is kept once at {backup}. " +
        "Remove restores it, taking out only these entries. No token is written.";
}
