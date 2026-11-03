using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

/// <summary>
/// The command a suite runs at a ring. Ring 0 runs in-process tests only (catalog.yml's header,
/// #375), so its .NET suites leave out the categories that start processes, bind sockets or need
/// a disposable Home Assistant. Later rings run the catalog's command as written.
/// </summary>
static class RingCommand
{
    public static readonly string[] Ring0Exclusions = ["Category!=Process", "Category!=Network", "Category!=HomeAssistantIntegration"];

    static readonly Regex Filter = new(@"--filter\s+""([^""]*)""");
    static readonly Regex DotnetTest = new(@"^\s*dotnet\s+test\b");

    public static string? For(string? command, int ring)
    {
        if (command is null || ring != 0 || !DotnetTest.IsMatch(command))
            return command;
        var match = Filter.Match(command);
        if (!match.Success)
            return $"{command} --filter \"{string.Join('&', Ring0Exclusions)}\"";
        var existing = match.Groups[1].Value;
        // A plain AND of terms merges, so homeassistant's own HomeAssistantIntegration term isn't
        // repeated; anything with | or parentheses is kept whole and ANDed.
        string[] terms = existing.IndexOfAny(['|', '(', ')']) >= 0
            ? [$"({existing})", .. Ring0Exclusions]
            : existing.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Concat(Ring0Exclusions).Distinct(StringComparer.Ordinal).ToArray();
        return command[..match.Index] + $"--filter \"{string.Join('&', terms)}\"" + command[(match.Index + match.Length)..];
    }
}