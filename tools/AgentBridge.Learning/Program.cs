using System.ComponentModel;
using System.Text.Json;
using System.Xml;

namespace AgentBridge.Learning;

static class Program
{
    const string Usage = """
        agent-bridge-learning: opt-in shared-learning experiment. Read-only; JSON on stdout.

          validate --note FILE
          capture --trx FILE --suite ID --head FULL_SHA --run-id ID --stage local|ci
          checkpoint --repo DIR --code-commit FULL_SHA --notes-commit FULL_SHA --tasks FILE
                     --harness NAME --harness-version VERSION --model NAME --model-version VERSION
          search --repo DIR --checkpoint FILE --tasks FILE --path PATH [--path PATH ...]
                 [--max-notes 12] [--max-bytes 12000]

        Notes are read only from quality/learning/notes at the explicitly trusted notes commit,
        never from the working tree. A status field is not proof of human approval: the operator
        must choose a reviewed notes commit. No command approves notes, installs hooks, calls a
        model, runs checks, accesses the network, writes files or changes Git state.
        search describes a pinned code snapshot, not an arbitrary working tree.
        See docs/shared-learning.md before creating an evaluation checkpoint.
        """;

    public static int Main(string[] args)
    {
        try { return Run(args, Console.Out); }
        catch (Exception e) when (e is ArgumentException or FormatException or IOException or InvalidOperationException
            or UnauthorizedAccessException or JsonException or XmlException or Win32Exception)
        {
            Console.Error.WriteLine($"agent-bridge-learning: {e.Message}");
            return 2;
        }
    }

    internal static int Run(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var allowed = args[0] switch
        {
            "validate" => new[] { "note" },
            "capture" => ["trx", "suite", "head", "run-id", "stage"],
            "checkpoint" => ["repo", "code-commit", "notes-commit", "tasks", "harness", "harness-version", "model", "model-version"],
            "search" => ["repo", "checkpoint", "tasks", "path", "max-notes", "max-bytes"],
            _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
        };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var paths = new List<string>();
        for (var i = 1; i < args.Length; i += 2)
        {
            var key = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : "";
            if (!allowed.Contains(key) || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected option or missing value: {args[i]}.");
            if (key == "path") paths.Add(args[i + 1]);
            else if (!options.TryAdd(key, args[i + 1])) throw new ArgumentException($"Repeated option --{key}.");
        }
        string Get(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Missing --{key}.");
        int Cap(string key, int fallback)
        {
            if (!options.TryGetValue(key, out var value)) return fallback;
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException($"--{key} must be a positive integer within the supported cap.");
            return parsed;
        }
        object report;
        var exit = 0;
        switch (args[0])
        {
            case "validate":
                var note = Note.Parse(File.ReadAllText(Get("note")));
                report = new { Version = 1, note.Id, note.Status, ShapeValid = true, ApprovalVerified = false, EvidenceLinksFetched = false };
                break;
            case "capture":
                var captured = TrxCapture.Parse(File.ReadAllBytes(Get("trx")), Get("suite"), Get("head"), Get("run-id"), Get("stage"));
                report = captured;
                exit = captured.Unparsed > 0 ? 1 : 0;
                break;
            case "checkpoint":
                report = Checkpoint.Create(new GitSnapshot(Get("repo")), Get("code-commit"), Get("notes-commit"),
                    File.ReadAllBytes(Get("tasks")), ToolHash(), Get("harness"), Get("harness-version"), Get("model"), Get("model-version"));
                break;
            default:
                var git = new GitSnapshot(Get("repo"));
                var checkpoint = JsonData.Read<Checkpoint>(File.ReadAllText(Get("checkpoint")));
                checkpoint.Verify(git, File.ReadAllBytes(Get("tasks")), ToolHash());
                var result = Retrieval.Search(git, checkpoint.CodeCommit, checkpoint.NotesCommit, paths.ToArray(),
                    DateOnly.FromDateTime(DateTime.UtcNow), Cap("max-notes", 12), Cap("max-bytes", 12_000));
                report = result;
                exit = result.Pending.Length > 0 || result.Withheld.Length > 0 || result.Overlaps.Length > 0 ? 1 : 0;
                break;
        }
        output.WriteLine(JsonData.Write(report));
        return exit;
    }

    static string ToolHash() => JsonData.Hash(File.ReadAllBytes(typeof(Program).Assembly.Location));
}
