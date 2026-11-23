using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

// A main run records, in its plan job, the base it diffs from and the in-flight parent run it relied on (#2454, item A of the Linux proposal).
// The judges (merge rule, main_green, the train selector) read this record to count a run green only when what it relied on is green too.
// This is the record only: no run relies on a parent yet, so every run records "relied-parent=none".
[Trait("Category", "Process")]
public class MainRelianceRecordTests
{
    // The real whole-ring step, run in a small repository with a stubbed gh. Commits: c0, c1, c2 (tagged v0.1.1, the published release), c3 = HEAD.
    // passed: the shas of main's passed CI runs, newest first (run ids 1000, 1001, ...). relied: for a passed run's sha, the plan-base record its
    // plan job annotated ("none" or a parent sha; absent = no record). broken: shas whose annotations cannot be read.
    static async Task<(string Stdout, string[] Commits)> RunStep(string passedAt, Func<string[], string>? passed = null,
        Func<string[], Dictionary<string, string>>? relied = null, Func<string[], string>? broken = null)
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
                if (name == "c2") { ChangedFiles.Git(folder, "tag", "v0.1.1"); }
            }

            var start = new ProcessStartInfo("pwsh") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["CI_PLAN_TEST_WHOLE"] = whole["run"].ToString();
            start.Environment["CI_PLAN_TEST_PASSED"] = passed is null ? passedAt : passed([.. commits]);
            start.Environment["CI_PLAN_TEST_RELIED"] = relied is null ? "" : string.Join(",", relied([.. commits]).Select(r => $"{r.Key}={r.Value}"));
            start.Environment["CI_PLAN_TEST_BROKEN"] = broken is null ? "" : broken([.. commits]);
            start.Environment["EVENT"] = "push";
            start.Environment["DRAFT"] = "false";
            start.Environment["REPO"] = "o/r";
            start.Environment["SHA"] = commits[3];
            start.Environment["GITHUB_OUTPUT"] = Path.Combine(folder, "outputs");
            start.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(folder, "summary");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         """
                         $ErrorActionPreference = 'Stop'
                         $stubPassed = @($env:CI_PLAN_TEST_PASSED -split ',' | Where-Object { $_ })
                         $stubRelied = @{}
                         foreach ($pair in ($env:CI_PLAN_TEST_RELIED -split ',' | Where-Object { $_ })) { $k, $v = $pair -split '=', 2; $stubRelied[$k] = $v }
                         function global:gh {
                             $global:LASTEXITCODE = 0
                             if ($args[1] -like '*actions/workflows/*') { for ($i = 0; $i -lt $stubPassed.Count; $i++) { "$(1000 + $i)`t$($stubPassed[$i])" } }
                             elseif ($args[1] -match '/actions/runs/(\d+)/jobs\?') { $Matches[1] }
                             elseif ($args[1] -match '/check-runs/(\d+)/annotations\?') {
                                 $stubSha = $stubPassed[[int]$Matches[1] - 1000]
                                 if ($env:CI_PLAN_TEST_BROKEN -split ',' -contains $stubSha) { $global:LASTEXITCODE = 1; return }
                                 if ($stubRelied.ContainsKey($stubSha)) { "base=$('a' * 40) relied-parent=$($stubRelied[$stubSha])" }
                             }
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

    // Item A, planner side: a passed run is a base only if every in-flight parent it relied on, back to a run that relied on none, passed too
    // (it is among the passed runs). Otherwise the parent's change was never tested, so the next run's diff carries it forward.
    static readonly string Gone = new('f', 40);

    static async Task<string> BaseOf(Func<string[], string> passed, Func<string[], Dictionary<string, string>>? relied = null, Func<string[], string>? broken = null)
    {
        var (stdout, commits) = await RunStep("", passed, relied, broken);
        var line = stdout.Split('\n').Single(l => l.Contains("::notice title=plan-base::", StringComparison.Ordinal));
        var found = commits.Select((sha, i) => (sha, i)).FirstOrDefault(c => line.Contains($"base={c.sha}", StringComparison.Ordinal));
        return found.sha is null ? line : $"c{found.i}";
    }

    [Fact]
    public async Task A_passed_run_that_relied_on_a_passed_parent_is_a_base()
        => Assert.Equal("c2", await BaseOf(c => $"{c[2]},{c[1]}", c => new() { [c[2]] = c[1], [c[1]] = "none" }));

    [Fact]
    public async Task A_passed_run_whose_parent_did_not_pass_is_not_a_base_the_next_older_one_is()
        => Assert.Equal("c1", await BaseOf(c => $"{c[2]},{c[1]}", c => new() { [c[2]] = Gone, [c[1]] = "none" }));

    [Fact]
    public async Task A_failure_further_back_the_chain_is_not_covered_either()
        => Assert.Equal("c0", await BaseOf(c => $"{c[2]},{c[1]},{c[0]}", c => new() { [c[2]] = c[1], [c[1]] = Gone, [c[0]] = "none" }));

    [Fact]
    public async Task A_reliance_loop_is_not_a_base()
        => Assert.Equal("c0", await BaseOf(c => $"{c[2]},{c[1]},{c[0]}", c => new() { [c[2]] = c[1], [c[1]] = c[2], [c[0]] = "none" }));

    [Fact]
    public async Task A_run_with_no_plan_record_relied_on_nothing_and_is_a_base()
        => Assert.Equal("c2", await BaseOf(c => $"{c[2]},{c[1]}"));

    [Fact]
    public async Task A_passed_run_whose_record_cannot_be_read_is_not_a_base_so_more_is_tested_not_less()
        => Assert.Equal("c1", await BaseOf(c => $"{c[2]},{c[1]}", c => new() { [c[1]] = "none" }, c => c[2]));

    [Fact]
    public async Task A_main_push_with_no_proven_base_records_the_first_parent_as_a_base_it_does_not_vouch_for()
    {
        // No passed run readable at all: the whole ring runs over HEAD^1; the record still says what the diff was taken from.
        var (stdout, commits) = await RunStep("");

        Assert.Contains($"::notice title=plan-base::base={commits[2]} relied-parent=none", stdout, StringComparison.Ordinal);
    }
}
