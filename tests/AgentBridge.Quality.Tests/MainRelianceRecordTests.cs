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
    static async Task<(string Stdout, string[] Commits, Dictionary<string, string> Outputs)> RunStep(string passedAt, Func<string[], string>? passed = null,
        Func<string[], Dictionary<string, string>>? relied = null, Func<string[], string>? broken = null, string parentRuns = "", int releaseAt = 2, bool parentUnreadable = false, string releases = "ok", string listed = "")
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
            var tags = releases != "tags" ? new List<(string Tag, int At)>() : listed.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split('@')).Select(t => (Tag: t[0], At: int.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture))).ToList();
            var commits = new List<string>();
            foreach (var name in new[] { "c0", "c1", "c2", "c3" })
            {
                File.WriteAllText(Path.Combine(folder, name + ".txt"), name);
                ChangedFiles.Git(folder, "add", "--", name + ".txt");
                ChangedFiles.Git(folder, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", name);
                commits.Add(ChangedFiles.Git(folder, "rev-parse", "HEAD").Trim());
                if ((releases is "ok" or "partial") && name == "c" + releaseAt) { ChangedFiles.Git(folder, "tag", "v0.1.1"); }
                // Explicit tags: "tag@commit,tag@commit" (releases = "tags"), each on the commit of that index.
                foreach (var tagAt in tags.Where(t => t.At == commits.Count - 1)) { ChangedFiles.Git(folder, "tag", tagAt.Tag); }
            }
            if (releases == "stray")
            {
                // A release whose commit is not an ancestor of HEAD: a tag on a side branch.
                ChangedFiles.Git(folder, "checkout", "-q", "-b", "side", commits[0]);
                File.WriteAllText(Path.Combine(folder, "side.txt"), "side");
                ChangedFiles.Git(folder, "add", "--", "side.txt");
                ChangedFiles.Git(folder, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "side");
                ChangedFiles.Git(folder, "tag", "v0.1.2");
                ChangedFiles.Git(folder, "checkout", "-q", "main");
            }

            var start = new ProcessStartInfo("pwsh") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["CI_PLAN_TEST_WHOLE"] = whole["run"].ToString();
            start.Environment["CI_PLAN_TEST_PASSED"] = passed is null ? passedAt : passed([.. commits]);
            start.Environment["CI_PLAN_TEST_RELIED"] = relied is null ? "" : string.Join(",", relied([.. commits]).Select(r => $"{r.Key}={r.Value}"));
            start.Environment["CI_PLAN_TEST_BROKEN"] = broken is null ? "" : broken([.. commits]);
            start.Environment["CI_PLAN_TEST_PARENT"] = parentRuns;
            start.Environment["CI_PLAN_TEST_PARENT_FAILS"] = parentUnreadable ? "1" : "";
            start.Environment["CI_PLAN_TEST_RELEASES"] = releases;
            start.Environment["CI_PLAN_TEST_RELEASE_LIST"] = string.Join(",", tags.Select(t => t.Tag));
            start.Environment["CI_PLAN_TEST_LOG"] = Path.Combine(folder, "gh.log");
            start.Environment["GH_TOKEN"] = "test-token"; // the workflow's github.token
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
                             if ($args[1] -like '*head_sha=*') {
                                 if ($env:CI_PLAN_TEST_PARENT_FAILS) { $global:LASTEXITCODE = 1; return }
                                 foreach ($row in ($env:CI_PLAN_TEST_PARENT -split ',' | Where-Object { $_ })) {
                                     $status, $age = $row -split ':'
                                     "$status`t$([datetime]::UtcNow.AddMinutes(-[int]$age).ToString('yyyy-MM-ddTHH:mm:ssZ'))"
                                 }
                             }
                             elseif ($args[1] -like '*actions/workflows/*') { for ($i = 0; $i -lt $stubPassed.Count; $i++) { "$(1000 + $i)`t$($stubPassed[$i])" } }
                             elseif ($args[1] -match '/actions/runs/(\d+)/jobs\?') { $Matches[1] }
                             elseif ($args[1] -match '/check-runs/(\d+)/annotations\?') {
                                 $stubSha = $stubPassed[[int]$Matches[1] - 1000]
                                 if ($env:CI_PLAN_TEST_BROKEN -split ',' -contains $stubSha) { $global:LASTEXITCODE = 1; return }
                                 if ($stubRelied.ContainsKey($stubSha)) { "base=$('a' * 40) relied-parent=$($stubRelied[$stubSha])" }
                             }
                             elseif ($args[1] -like '*releases?*') {
                                 Add-Content -LiteralPath $env:CI_PLAN_TEST_LOG -Value "releases token=$([bool]$env:GH_TOKEN) $($args -join ' ')"
                                 switch ($env:CI_PLAN_TEST_RELEASES) {
                                     'unreadable' { $global:LASTEXITCODE = 1; return }
                                     'partial' { 'v0.1.1'; $global:LASTEXITCODE = 1; return }
                                     'nomatch' { 'nightly'; 'v1.0'; 'v0.1.x' }
                                     'gone' { 'v9.9.9' }
                                     'stray' { 'v0.1.2' }
                                     'tags' { $env:CI_PLAN_TEST_RELEASE_LIST -split ',' }
                                     default { 'v0.1.1' }
                                 }
                             }
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
            var outputs = File.ReadAllLines(Path.Combine(folder, "outputs")).Select(line => line.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            var log = File.Exists(Path.Combine(folder, "gh.log")) ? File.ReadAllLines(Path.Combine(folder, "gh.log")) : [];
            outputs["gh-release-reads"] = log.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            outputs["gh-release-reads-with-token"] = log.Count(l => l.Contains("token=True", StringComparison.Ordinal)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (await stdout, [.. commits], outputs);
        }
        finally
        {
            Fixture.Delete(folder);
        }
    }

    [Fact]
    public async Task A_main_push_records_the_base_it_diffs_from_and_that_it_relied_on_no_parent()
    {
        var (stdout, commits, _) = await RunStep("", shas => shas[1]);

        Assert.Contains($"::notice title=plan-base::base={commits[1]} relied-parent=none", stdout, StringComparison.Ordinal);
    }

    // Item A, planner side: a passed run is a base only if every in-flight parent it relied on, back to a run that relied on none, passed too
    // (it is among the passed runs). Otherwise the parent's change was never tested, so the next run's diff carries it forward.
    static readonly string Gone = new('f', 40);

    static async Task<string> BaseOf(Func<string[], string> passed, Func<string[], Dictionary<string, string>>? relied = null, Func<string[], string>? broken = null,
        string parentRuns = "", int releaseAt = 2, bool parentUnreadable = false)
    {
        var (stdout, commits, _) = await RunStep("", passed, relied, broken, parentRuns, releaseAt, parentUnreadable);
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

    // Item A, the shortcut: while the first parent's run is still going, this run tests only its own diff (base = the parent) and records the
    // parent as what it relied on. Anything else keeps the passed base: a failed or cancelled parent carries its change forward, as now.
    static async Task<(string Base, string Relied)> Shortcut(string parentRuns, int releaseAt = 2, bool parentUnreadable = false,
        Func<string[], string>? passed = null)
    {
        var (stdout, commits, _) = await RunStep("", passed ?? (c => $"{c[1]},{c[0]}"), null, null, parentRuns, releaseAt, parentUnreadable);
        var line = stdout.Split('\n').Single(l => l.Contains("::notice title=plan-base::", StringComparison.Ordinal));
        var baseSha = commits.Select((sha, i) => (sha, i)).First(c => line.Contains($"base={c.sha}", StringComparison.Ordinal));
        var relied = line.Contains("relied-parent=none", StringComparison.Ordinal) ? "none"
            : commits.Select((sha, i) => (sha, i)).First(c => line.Contains($"relied-parent={c.sha}", StringComparison.Ordinal)).i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ($"c{baseSha.i}", relied);
    }

    [Theory]
    [InlineData("in_progress:10")]
    [InlineData("queued:3")]
    [InlineData("waiting:40")]
    public async Task A_first_parent_still_running_is_relied_on_so_this_run_tests_only_its_own_diff(string parentRuns)
        => Assert.Equal(("c2", "2"), await Shortcut(parentRuns));

    [Theory]
    [InlineData("in_progress:180")]
    [InlineData("completed:10")]
    [InlineData("completed:600")]
    [InlineData("")]
    public async Task A_first_parent_not_running_within_the_cap_is_not_relied_on_the_passed_base_stands(string parentRuns)
        => Assert.Equal(("c1", "none"), await Shortcut(parentRuns));

    [Fact]
    public async Task A_first_parent_that_is_running_again_after_an_earlier_run_is_relied_on()
        => Assert.Equal(("c2", "2"), await Shortcut("completed:90,in_progress:5"));

    [Fact]
    public async Task First_parent_runs_that_cannot_be_read_fall_back_to_the_passed_base()
        => Assert.Equal(("c1", "none"), await Shortcut("in_progress:10", parentUnreadable: true));

    // A2: release debt no longer lowers the base the shortcut and every job but packaging use. A release older than the parent widens only
    // the packaging base (below), so the run still relies on its parent.
    [Fact]
    public async Task A_release_older_than_the_parent_no_longer_lowers_the_base_the_run_relies_on_its_parent()
        => Assert.Equal(("c2", "2"), await Shortcut("in_progress:10", releaseAt: 0));

    static async Task<(string Base, string Packaging)> Bases(int releaseAt, string parentRuns = "", Func<string[], string>? passed = null)
    {
        var (_, commits, outputs) = await RunStep("", passed ?? (c => $"{c[1]},{c[0]}"), null, null, parentRuns, releaseAt, false);
        string Name(string sha) => "c" + Array.IndexOf(commits, sha);
        return (Name(outputs["base"]), Name(outputs["packaging-base"]));
    }

    // Release debt (#1910): earlier untested or unpaid app changes still build. It now widens ONLY packaging: the app and installer jobs read the
    // packaging base, the older of the base and the latest published release; every other job reads the base.
    [Fact]
    public async Task Release_debt_widens_only_the_packaging_base()
        => Assert.Equal(("c1", "c0"), await Bases(releaseAt: 0));

    [Fact]
    public async Task A_release_newer_than_the_base_leaves_both_the_same()
        => Assert.Equal(("c1", "c1"), await Bases(releaseAt: 2));

    [Fact]
    public async Task The_shortcut_base_is_the_parent_while_packaging_still_reaches_back_to_the_release()
        => Assert.Equal(("c2", "c0"), await Bases(releaseAt: 0, "in_progress:10"));

    // Quality's conditions on the release read (#2454, item A2): every failure WIDENS. Nothing proved about the release, or no passed run: the base is
    // HEAD^1, the whole ring runs, and packaging is un-narrowed (base-known=false). Each case is one way the read can fail.
    static async Task AssertWidened(string releases = "ok", bool noPassedRun = false)
    {
        var (stdout, commits, outputs) = await RunStep("", c => noPassedRun ? "" : $"{c[1]},{c[0]}", releases: releases);
        Assert.Equal("false", outputs["base-known"]);
        Assert.Equal("true", outputs["whole-ring"]);
        Assert.Equal("HEAD^1", outputs["base"]);
        Assert.Equal("HEAD^1", outputs["packaging-base"]);
        Assert.Contains($"base={commits[2]} relied-parent=none", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public Task A_release_list_that_cannot_be_read_widens_to_the_whole_ring() => AssertWidened("unreadable");

    // gh printed a page, then failed: a listing that is paged short by an error. The tag it did name must not be trusted.
    [Fact]
    public Task A_release_list_that_fails_part_way_widens_to_the_whole_ring() => AssertWidened("partial");

    [Fact]
    public Task A_release_list_with_no_matching_tag_widens_to_the_whole_ring() => AssertWidened("nomatch");

    [Fact]
    public Task A_release_tag_that_does_not_resolve_widens_to_the_whole_ring() => AssertWidened("gone");

    [Fact]
    public Task A_release_tag_that_is_not_an_ancestor_widens_to_the_whole_ring() => AssertWidened("stray");

    [Fact]
    public Task No_passed_run_widens_to_the_whole_ring() => AssertWidened(noPassedRun: true);

    [Fact]
    public async Task A_proved_release_keeps_the_base_known_and_narrowed()
    {
        var (_, commits, outputs) = await RunStep("", c => $"{c[1]},{c[0]}");
        Assert.Equal("true", outputs["base-known"]);
        Assert.Equal("false", outputs["whole-ring"]);
        Assert.Equal(commits[1], outputs["base"]);
    }

    // The newest release is the newest by SEMVER, not by the API's order (newest created first) or by text: v0.1.7690 > v0.1.7684 > v0.1.99
    // numerically (by text v0.1.99 would win). Tags: v0.1.7684 on c0, v0.1.99 on c1, v0.1.7690 on c2; the base is c2 (the passed parent), so the
    // packaging base names the tag chosen: c2 for v0.1.7690.
    [Theory]
    [InlineData("v0.1.99@1,v0.1.7690@2,v0.1.7684@0")]
    [InlineData("v0.1.7684@0,v0.1.7690@2,v0.1.99@1")]
    [InlineData("v0.1.7690@2,v0.1.99@1,v0.1.7684@0")]
    public async Task The_newest_release_is_the_newest_by_semver_not_by_the_apis_order(string listed)
    {
        var (_, commits, outputs) = await RunStep("", c => $"{c[2]},{c[1]},{c[0]}", releases: "tags", listed: listed);
        Assert.Equal(commits[2], outputs["base"]);
        Assert.Equal(commits[2], outputs["packaging-base"]);
    }

    // A listing paged short can only lose tags: the newest it still names is older or the same, so the packaging base is older (wider), never newer.
    // Without v0.1.7690 (on c2) the newest left is v0.1.7684 (on c0): the packaging base falls back to c0.
    [Fact]
    public async Task A_release_list_paged_short_gives_an_older_release_and_a_wider_packaging_base()
    {
        var (_, commits, outputs) = await RunStep("", c => $"{c[2]},{c[1]},{c[0]}", releases: "tags", listed: "v0.1.99@1,v0.1.7684@0");
        Assert.Equal(commits[2], outputs["base"]);
        Assert.Equal(commits[0], outputs["packaging-base"]);
    }

    // The older of the two, both ways: a release newer than the passed base leaves the base; one older than it widens to the release.
    [Theory]
    [InlineData("v0.1.5@2", 1, 1)] // release c2 is newer than the passed run c1: packaging base = c1
    [InlineData("v0.1.5@0", 1, 0)] // release c0 is older than the passed run c1: packaging base = c0
    public async Task Packaging_reaches_back_to_the_older_of_the_passed_base_and_the_release(string listed, int baseAt, int packagingAt)
    {
        var (_, commits, outputs) = await RunStep("", c => $"{c[baseAt]}", releases: "tags", listed: listed);
        Assert.Equal(commits[baseAt], outputs["base"]);
        Assert.Equal(commits[packagingAt], outputs["packaging-base"]);
    }

    // The release list is read ONCE per run, with the workflow's token (never anonymous), and a draft is ignored while a pre-release counts: the
    // filter is gh's --jq on the API's draft flag only, not on prerelease. (gh evaluates that expression, so the test reads it from the step.)
    [Fact]
    public async Task The_release_list_is_read_once_with_the_workflows_token()
    {
        var (_, _, outputs) = await RunStep("", c => $"{c[1]},{c[0]}");
        Assert.Equal("1", outputs["gh-release-reads"]);
        Assert.Equal("1", outputs["gh-release-reads-with-token"]);
    }

    [Fact]
    public void The_step_reads_releases_with_the_workflows_token_ignores_drafts_and_counts_pre_releases()
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))));
        var steps = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["jobs"])["plan"])["steps"];
        var whole = steps.Children.Cast<YamlMappingNode>().Single(step =>
            step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "whole-ring");
        Assert.Equal("${{ github.token }}", ((YamlMappingNode)whole["env"])["GH_TOKEN"].ToString());
        var run = whole["run"].ToString();
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(run, @"releases\?per_page=100"));
        Assert.Contains("select(.draft == false)", run, StringComparison.Ordinal);
        Assert.DoesNotContain("prerelease", run, StringComparison.Ordinal);
        Assert.Contains("[semver]", run, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_main_push_with_no_proven_base_records_the_first_parent_as_a_base_it_does_not_vouch_for()
    {
        // No passed run readable at all: the whole ring runs over HEAD^1; the record still says what the diff was taken from.
        var (stdout, commits, _) = await RunStep("");

        Assert.Contains($"::notice title=plan-base::base={commits[2]} relied-parent=none", stdout, StringComparison.Ordinal);
    }
}
