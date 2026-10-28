namespace AgentBridge.Host.Core.Parsing;

// Copilot's workspace.yaml: flat `key: value` lines (id, cwd, git_root, repository,
// branch, name, created_at, updated_at). Only top-level scalars are read, which is all
// the CLI writes; anything nested or unrecognised is ignored rather than failing.
internal sealed record Workspace(string? Name, string? Cwd, string? Repository, string? Branch, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt)
{
    public static Workspace? Read(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 256 * 1024) { return null; }
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return Parse(text);
    }

    public static Workspace Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-') { continue; }
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) { continue; }
            values[line[..colon]] = Scalar(line[(colon + 2)..]);
        }
        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new Workspace(Get("name"), Get("cwd"), Get("repository"), Get("branch"), Json.ParseTime(Get("created_at")), Json.ParseTime(Get("updated_at")));
    }

    static string Scalar(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') { return value[1..^1].Replace("''", "'"); }
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? ""; }
            catch (System.Text.Json.JsonException) { return value[1..^1]; }
        }
        // A plain scalar ends at a comment.
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0) { value = value[..comment].TrimEnd(); }
        return value is "~" or "null" ? "" : value;
    }
}
