namespace AgentBridge.Quality.Tests;

// quality/test-pages-gate.ps1 counts its failures and must end on their exit: #4370's first head lost that tail, and the
// file then passed whatever it found. Pinned from outside the file, since a lost tail loses any check inside it too (#4425).
public class PagesGateScriptTests
{
    static string Quality(string file) => Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", file);

    [Fact]
    public void The_pages_gate_tests_end_by_failing_on_any_failure_and_never_exit_0()
    {
        var lines = File.ReadAllLines(Quality("test-pages-gate.ps1")).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        Assert.Equal("if ($failures) { exit 1 }", lines[^1]);
        Assert.DoesNotContain(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"(^|[;{]\s*)exit\s+0\b"));
    }

    // By design, not fixed: a renamed or expired 'Pages rollback' run leaves the fence open. It stays written down where
    // the fence is decided.
    [Fact]
    public void The_rollback_fences_exact_title_edge_stays_documented()
    {
        var header = string.Join("\n", File.ReadAllLines(Quality("pages-rollback.ps1")).TakeWhile(l => l.StartsWith('#')));
        Assert.Contains("matched by exact run title", header, StringComparison.Ordinal);
        Assert.Contains("renamed title", header, StringComparison.Ordinal);
    }
}
