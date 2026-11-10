using System.Text.RegularExpressions;
using Xunit;

namespace AgentBridge.Host.Tests;

// A child the host or app starts must never open a console window (#1528): with
// UseShellExecute off, a console program still gets a window of its own (or a Windows
// Terminal tab) unless CreateNoWindow is set. A session's own console is started by
// WindowsAgentStarter (CreateProcess), deliberately visible, and is not a ProcessStartInfo.
public class HeadlessChildTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return root;
    }

    [Fact]
    public void Every_process_the_product_starts_is_started_without_a_console_window()
    {
        var start = new Regex(@"new\s+(System\.Diagnostics\.)?ProcessStartInfo\b", RegexOptions.CultureInvariant);
        var launch = new Regex(@"\bProcess(Launch)?\.Start\(|\.Start\(\)", RegexOptions.CultureInvariant);
        var windowed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            var code = File.ReadAllText(file);
            foreach (Match site in start.Matches(code))
            {
                // From the construction to the call that starts it.
                var end = launch.Match(code, site.Index);
                var statement = code[site.Index..(end.Success ? end.Index : code.Length)];
                if (!statement.Contains("CreateNoWindow = true", StringComparison.Ordinal))
                {
                    windowed.Add($"{Path.GetRelativePath(Root, file)}:{code[..site.Index].Count(c => c == '\n') + 1}");
                }
            }
        }

        Assert.Empty(windowed);
    }
}
