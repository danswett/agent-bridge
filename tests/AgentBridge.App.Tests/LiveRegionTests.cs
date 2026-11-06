using Xunit;

namespace AgentBridge.App.Tests;

// A live region set from code says nothing until its change is raised: WinUI does not raise
// LiveRegionChanged when a TextBlock's text changes, so a screen reader never heard the escaped
// approval's "Approve once is available" (#836 review). Each file that makes one raises it.
public class LiveRegionTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        return root;
    }

    [Fact]
    public void Every_live_region_the_app_makes_in_code_is_announced_by_raising_its_change()
    {
        var silent = Directory.EnumerateFiles(Path.Combine(Root, "src", "AgentBridge.App"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var code && code.Contains("SetLiveSetting(", StringComparison.Ordinal) && !code.Contains("AutomationEvents.LiveRegionChanged", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Root, f));

        Assert.Empty(silent);
    }
}
