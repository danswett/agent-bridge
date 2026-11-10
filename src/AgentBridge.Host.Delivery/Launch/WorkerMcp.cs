using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Delivery;

// What a worker launched for a role keeps of the person's own MCP servers (#1525). Copilot loads
// everything in ~/.copilot/mcp-config.json and has no flag for "only these", so the host reads
// the server names there at every launch (never cached: a server added since is still denied)
// and passes --disable-mcp-server for each one the role's launch.workerMcp entry does not list.
// A role with no entry keeps none. Only names are read and counted; no name or definition is
// ever logged. A server added between this read and the agent's own read of the file would
// load: a window of milliseconds, accepted rather than guarded with a timer or a copy of the
// whole Copilot home.
internal static partial class WorkerMcp
{
    public const string File = "mcp-config.json";
    // Copilot's own servers, switched off as a group when the role lists neither.
    static readonly string[] BuiltIn = ["github-mcp-server", "githubiq"];

    public sealed record Result(IReadOnlyList<string> Arguments, int Disabled, int Kept, string? Refusal);

    public static Result For(string copilotHome, IReadOnlyDictionary<string, IReadOnlyList<string>> allowed, string role)
    {
        var keep = allowed.TryGetValue(role, out var list) ? list : [];
        var names = new List<string>();
        var path = Path.Combine(copilotHome, File);
        try
        {
            if (System.IO.File.Exists(path))
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var s in servers.EnumerateObject()) { names.Add(s.Name); }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Starting anyway would load whatever the file holds into a worker.
            return new Result([], 0, 0, "could not read the MCP server list to narrow it for this role; nothing was started");
        }

        var args = new List<string>();
        var disabled = 0;
        var kept = 0;
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (keep.Contains(name, StringComparer.Ordinal)) { kept++; continue; }
            // Passed as a separate argument, so a name Copilot could read as a flag cannot be
            // denied safely; refuse rather than leave that server loading.
            if (!Safe().IsMatch(name)) { return new Result([], 0, 0, "an MCP server in the person's list has a name that cannot be disabled safely; nothing was started"); }
            args.AddRange(["--disable-mcp-server", name]);
            disabled++;
        }

        var builtIns = BuiltIn.Where(b => keep.Contains(b, StringComparer.Ordinal)).ToList();
        if (builtIns.Count == 0) { args.Add("--disable-builtin-mcps"); }
        else
        {
            foreach (var b in BuiltIn.Except(builtIns)) { args.AddRange(["--disable-mcp-server", b]); }
        }
        return new Result(args, disabled, kept, null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$")]
    private static partial Regex Safe();
}
