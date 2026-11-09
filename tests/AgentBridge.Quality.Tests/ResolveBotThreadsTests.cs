using System.Diagnostics;

namespace AgentBridge.Quality.Tests;

// quality/resolve-bot-threads.ps1's reading of merge-rule.yml (-ParseRuleOnly stops before any
// GitHub call). It starts pwsh, so ring 0 leaves it to ring 1.
[Trait("Category", "Process")]
public class ResolveBotThreadsTests
{
    const string Good = "independent_review:\n  context: independent-review\n  posters: [danswett]\n\ncopilot_waiver:\n  second_review: independent-review-2\n";

    [Fact]
    public void The_real_rule_file_parses()
    {
        var (exit, output) = Run(File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "merge-rule.yml")));

        Assert.Equal(0, exit);
        Assert.Contains("independent-review-2", output);
    }

    [Theory]
    [InlineData("independent_review:\n  context: independent-review-2\n  posters: [danswett]\n\ncopilot_waiver:\n  second_review: independent-review-2\n")]
    [InlineData("independent_review:\n  context: independent-review\n  posters: [danswett]\n\ncopilot_waiver:\n  second_review: independent-review-22\n")]
    [InlineData("independent_review:\n  context: independent-review\n  posters: [danswett]\n\ncopilot_waiver:\n  second_review: independent-review\n")]
    // Keys only in a comment, or in the wrong section.
    [InlineData("# context: independent-review\nindependent_review:\n  posters: [danswett]\n\ncopilot_waiver:\n  second_review: independent-review-2\n")]
    [InlineData("independent_review:\n  context: independent-review\n  posters: [danswett]\n\ncopilot_waiver:\n  # second_review: independent-review-2\n")]
    [InlineData("other:\n  posters: [danswett]\nindependent_review:\n  context: independent-review\n\ncopilot_waiver:\n  second_review: independent-review-2\n")]
    public void A_near_miss_in_the_rule_file_is_refused(string rule)
    {
        var (exit, _) = Run(rule);

        Assert.NotEqual(0, exit);
    }

    [Fact]
    public void The_exact_keys_are_read()
    {
        var (exit, output) = Run(Good);

        Assert.Equal(0, exit);
        Assert.Contains("\"posters\":\"danswett\"", output);
    }

    static (int Exit, string Output) Run(string rule)
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = Path.Combine(folder, "rule.yml");
            File.WriteAllText(file, rule);
            var script = Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), "quality", "resolve-bot-threads.ps1");
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Pr", "1", "-RuleFile", file, "-ParseRuleOnly" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output + error.Result);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
