using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

// The critical-fix override (docs/app-windows.md, "Spacing"): a run of ci.yml by hand passes the
// plan job's guard as a critical release only on main and only with a reason, before anything is
// read or built; and the publish path it opens keeps publish-gate's form (CiPlan: no ||).
public class CriticalReleaseTests
{
    const string Override = "!(github.event_name == 'workflow_dispatch' && !inputs.critical-release)";

    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);

    static YamlMappingNode Ci()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"))));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    static YamlMappingNode Job(string name) => (YamlMappingNode)((YamlMappingNode)Ci()["jobs"])[name];

    static YamlMappingNode Step(string job, string namePrefix) => ((YamlSequenceNode)Job(job)["steps"]).Children.Cast<YamlMappingNode>()
        .Single(s => s.Children.TryGetValue(new YamlScalarNode("name"), out var n) && n.ToString().StartsWith(namePrefix, StringComparison.Ordinal));

    static (int Code, string Output) RunGuard(string critical, string reason, string reference, string force = "false")
    {
        // Git's bash on Windows, as GitHub's Windows runners have it; the guard itself runs on Ubuntu.
        var bash = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
            : "/bin/bash";
        // gh, if the guard reaches it, says so and fails: a critical release decides before reading anything.
        var script = "gh() { echo 'gh was called'; return 1; }\n" + Step("plan", "A run by hand")["run"];
        var start = new ProcessStartInfo(bash, ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (key, value) in new[] { ("CRITICAL", critical), ("REASON", reason), ("REF", reference), ("FORCE", force),
                     ("SHA", new string('a', 40)), ("REPO", "danswett/agent-bridge"), ("RUNNER_TEMP", Path.GetTempPath()) })
            start.Environment[key] = value;
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void A_critical_release_on_main_with_a_reason_passes_the_plan_guard_without_reading_anything()
    {
        var (code, output) = RunGuard("true", "the relay drops every message", "refs/heads/main");
        Assert.True(code == 0, output);
        Assert.DoesNotContain("gh was called", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "", "refs/heads/main", "false", "critical-release needs critical-reason")]
    [InlineData("true", " \t\n", "refs/heads/main", "false", "critical-release needs critical-reason")]
    [InlineData("true", "a reason", "refs/heads/fix", "false", "critical-release only on main")]
    [InlineData("true", "a reason", "refs/heads/main", "true", "separate runs")]
    [InlineData("false", "a reason", "refs/heads/main", "false", "a run by hand is only for force-apt-fallback=true")]
    public void Any_other_run_by_hand_on_main_is_refused_before_the_build(string critical, string reason, string reference, string force, string says)
    {
        var (code, output) = RunGuard(critical, reason, reference, force);
        Assert.True(code != 0, output);
        Assert.Contains(says, output, StringComparison.Ordinal);
        Assert.DoesNotContain("gh was called", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_publish_path_is_a_push_or_a_critical_release_on_main_and_the_reason_reaches_publish_through_the_environment()
    {
        var gate = Job("publish-gate")["if"].ToString();
        Assert.DoesNotContain("||", gate, StringComparison.Ordinal);
        foreach (var expression in new[] { gate, ((YamlMappingNode)Ci()["env"])["APP_RELEASE"].ToString(), ((YamlMappingNode)Ci()["env"])["APP_SIGNING"].ToString() })
        {
            Assert.Contains(Override, expression, StringComparison.Ordinal);
            Assert.Contains("github.ref == 'refs/heads/main'", expression, StringComparison.Ordinal);
            Assert.Contains("github.event_name != 'pull_request'", expression, StringComparison.Ordinal);
        }
        var publish = Step("publish", "Publish the pre-release");
        var run = publish["run"].ToString();
        Assert.Contains("$sign.SkipSpacingReason = $env:CRITICAL_REASON", run, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.", run, StringComparison.Ordinal);
        Assert.Equal("${{ inputs.critical-reason }}", ((YamlMappingNode)publish["env"])["CRITICAL_REASON"].ToString());
    }
}
