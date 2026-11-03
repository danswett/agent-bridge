using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

// The committed protocol vectors in docs/protocol/vectors (README.md there), shared by C#, the
// PWA and, later, Swift. Linked into AgentBridge.Host.Tests too, which writes the signed-command
// vectors, since those need the host's keys.
public static class VectorFiles
{
    // Set to 1 to write the vectors the records generate over the committed ones, rather than
    // failing when they differ.
    public const string UpdateVariable = "UPDATE_PROTOCOL_VECTORS";

    public static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        // The vectors are read by people too: text stays as it is, emoji aside, as on the wire.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Folder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentBridge.slnx"))) { return Path.Combine(dir.FullName, "docs", "protocol", "vectors"); }
            }
            throw new DirectoryNotFoundException("the repository (AgentBridge.slnx) is not above " + AppContext.BaseDirectory);
        }
    }

    public static string Text(JsonNode file) => file.ToJsonString(Format).ReplaceLineEndings("\n") + "\n";

    public static JsonObject Read(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name)))!.AsObject();

    // The file names whose committed text is not what was generated. With UPDATE_PROTOCOL_VECTORS=1
    // they are written instead, and none is reported.
    public static IReadOnlyList<string> Stale(IReadOnlyDictionary<string, JsonObject> generated)
    {
        var update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        var stale = new List<string>();
        foreach (var (name, json) in generated)
        {
            var path = Path.Combine(Folder, name);
            var text = Text(json);
            // A checkout may have turned the line endings into CRLF.
            var committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
            if (committed == text) { continue; }
            if (update)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, text);
            }
            else { stale.Add(name); }
        }
        return stale;
    }
}
