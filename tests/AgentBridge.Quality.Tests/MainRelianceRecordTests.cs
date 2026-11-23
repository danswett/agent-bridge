using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

// A main run records, in its plan job, the base it diffs from and the in-flight parent run it relied on (#2454, item A of the Linux proposal).
// The judges (merge rule, main_green, the train selector) read this record to count a run green only when what it relied on is green too.
// This is the record only: no run relies on a parent yet, so every run records "relied-parent=none".
[Trait("Category", "Process")]
public class MainRelianceRecordTests
{
    // The real whole-ring step, run in a small repository with a stubbed gh. Commits: c0, c1 (tagged v0.1.1, the published release), c2, c3 = HEAD.
    static async Task<(string Stdout, string[] Commits)> RunStep(string passedAt, Func<string[], string>? passed = null)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var folder = Fixture.ScratchFolder();
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))));
            var steps = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["jobs"])["plan"])["steps"];
            var whole = steps.Children.Cast<YamlMappingNode>().Single(step =>
                step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "whole-ring");

            ChangedFiles.Git(folder, "init", "-q", "-b", "main");
            var commits = new List<string>();
            foreach (var name in new[] { "c0", "c1", "c2", "c3" })
            {
                File.WriteAllText(Path.Combine(folder, name + ".txt"), name);
                ChangedFiles.Git(folder, "add", "--", name + ".txt");
                ChangedFiles.Git(folder, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", name);
                commits.Add(ChangedFiles.Git(folder, "rev-parse", "HEAD").Trim());
                if (name == "c1") { ChangedFiles.Git(folder, "tag", "v0.1.1"); }
            }

            var start = new ProcessStartInfo("pwsh") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["CI_PLAN_TEST_WHOLE"] = whole["run"].ToString();
            start.Environment["CI_PLAN_TEST_PASSED"] = passed is null ? passedAt : passed([.. commits]);
            start.Environment["EVENT"] = "push";
            start.Environment["DRAFT"] = "false";
            start.Environment["REPO"] = "o/r";
            start.Environment["SHA"] = commits[3];
            start.Environment["GITHUB_OUTPUT"] = Path.Combine(folder, "outputs");
            start.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(folder, "summary");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         """
                         $ErrorActionPreference = 'Stop'
                         function global:gh {
                             $global:LASTEXITCODE = 0
                             if ($args[1] -like '*actions/workflows/*') { $env:CI_PLAN_TEST_PASSED -split ',' | Where-Object { $_ } }
                             elseif ($args[1] -like '*releases?*') { 'v0.1.1' }
                             else { throw "Unexpected offline gh call: $($args -join ' ')" }
                         }
                         & ([scriptblock]::Create($env:CI_PLAN_TEST_WHOLE))
                         """ })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var error = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, (await error) + (await stdout));
            return (await stdout, [.. commits]);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Fact]
    public async Task A_main_push_records_the_base_it_diffs_from_and_that_it_relied_on_no_parent()
    {
        var (stdout, commits) = await RunStep("", shas => shas[1]);

        Assert.Contains($"::notice title=plan-base::base={commits[1]} relied-parent=none", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_main_push_with_no_proven_base_records_the_first_parent_as_a_base_it_does_not_vouch_for()
    {
        // No passed run readable at all: the whole ring runs over HEAD^1; the record still says what the diff was taken from.
        var (stdout, commits) = await RunStep("");

        Assert.Contains($"::notice title=plan-base::base={commits[2]} relied-parent=none", stdout, StringComparison.Ordinal);
    }
}
