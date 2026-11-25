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

    // The grant list's status line is announced once, through ReceiptMarkView.SetAnnouncement, whose
    // announcer gates the first draw, hushed and stale changes. A second path (a direct live setting
    // or a raised LiveRegionChanged) would say it twice and skip that gating. A source guard: the
    // WinUI view cannot be built here, so this is not a UIA event count.
    [Fact]
    public void Grant_list_status_is_announced_once_through_the_announcer()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "AgentBridge.App", "GrantListView.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"ReceiptMarkView\.SetAnnouncement\(status,"));
        Assert.DoesNotContain("SetLiveSetting(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("RaiseAutomationEvent(", code, StringComparison.Ordinal);
    }
}
