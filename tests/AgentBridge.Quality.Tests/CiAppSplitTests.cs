using YamlDotNet.RepresentationModel;
using Xunit;

namespace AgentBridge.Quality.Tests;

// #4104: the x64 desktop self-test and the SBOM left ci.yml's `app` job, so that job (the FAST build and pack, whose required check
// name stays `app (win-x64)`) can take the ab-ci pool, whose services have no desktop and need not reach Trivy's download. These read
// the real workflows: the self-test still runs on a pull request that touches the app and after every merge in full.yml, the SBOM keeps
// the artifact name publish and attest read, and what needs a desktop or Trivy stays hosted.
public sealed class CiAppSplitTests
{
    static string Root => ChangedFiles.RepoRoot(AppContext.BaseDirectory);

    static YamlMappingNode Jobs(string file)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(Root, ".github", "workflows", file))));
        return (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["jobs"];
    }

    static YamlMappingNode Job(string file, string id) => (YamlMappingNode)Jobs(file)[id];
    static List<YamlMappingNode> Steps(YamlMappingNode job) => ((YamlSequenceNode)job["steps"]).Children.Cast<YamlMappingNode>().ToList();
    static string? Field(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : null;
    static List<string> Needs(YamlMappingNode job) => job["needs"] switch
    {
        YamlSequenceNode many => many.Children.Select(n => n.ToString()).ToList(),
        var one => new List<string> { one.ToString() },
    };

    [Fact]
    public void App_win_x64_keeps_its_required_name_and_only_the_arm64_leg_runs_the_self_test_in_it()
    {
        var app = Job("ci.yml", "app");
        Assert.Equal("app (${{ matrix.runtime }})", Field(app, "name"));
        var steps = Steps(app);
        var selfTest = steps.Where(s => (Field(s, "run") ?? "").Contains("app-self-test.ps1", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(selfTest);
        Assert.All(selfTest, s => Assert.Contains("win-arm64", Field(s, "if") ?? "", StringComparison.Ordinal));
        // The staging and upload of the diagnostics follow the self-test, so they are the Arm64 leg's as well.
        Assert.Contains("win-arm64", Field(steps.Single(s => Field(s, "id") == "selftest-text"), "if"), StringComparison.Ordinal);
        // The SBOM and the Trivy install moved to app-sbom for x64; Arm64's stay (publish-gate reads sbom-win-arm64).
        foreach (var s in steps.Where(s => Field(s, "name") is "Install Trivy" or "SBOM of the published app (CycloneDX)"))
            Assert.Equal("matrix.runtime == 'win-arm64'", Field(s, "if"));
    }

    [Fact]
    public void App_restores_and_publishes_through_the_runner_mirror_with_vpk_in_a_folder_of_its_own()
    {
        var steps = Steps(Job("ci.yml", "app"));
        var all = string.Join('\n', steps.Select(s => Field(s, "run") ?? ""));
        Assert.DoesNotContain("tool install -g", all, StringComparison.Ordinal);
        Assert.Contains("--tool-path", all, StringComparison.Ordinal);
        Assert.Contains("--configfile", all, StringComparison.Ordinal);
        var mirror = steps.Single(s => (Field(s, "run") ?? "").Contains("New-AbCiNuGetConfig", StringComparison.Ordinal));
        Assert.Equal("runner.environment == 'self-hosted'", Field(mirror, "if"));
        // The per-job state comes before any local action or dotnet, and the last step stops the build servers (CachePolicy).
        var state = steps.FindIndex(s => (Field(s, "run") ?? "").Contains("NUGET_PACKAGES=", StringComparison.Ordinal));
        Assert.InRange(state, 1, steps.FindIndex(s => (Field(s, "uses") ?? "").StartsWith("./", StringComparison.Ordinal)));
        Assert.Equal("always()", Field(steps[^1], "if"));
        Assert.Contains("dotnet build-server shutdown", Field(steps[^1], "run"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_x64_publish_folder_is_always_uploaded_for_seven_days_so_the_self_test_and_the_sbom_test_the_bytes_that_were_packed()
    {
        var upload = Steps(Job("ci.yml", "app")).Single(s => (Field(s, "name") ?? "").StartsWith("The publish folder", StringComparison.Ordinal));
        Assert.Contains("matrix.runtime == 'win-x64'", Field(upload, "if"), StringComparison.Ordinal);
        var with = (YamlMappingNode)upload["with"];
        Assert.Equal("${{ matrix.artifact }}-publish-${{ env.APP_VERSION }}", Field(with, "name"));
        Assert.Equal("${{ matrix.out }}/publish", Field(with, "path"));
        Assert.Equal("7", Field(with, "retention-days"));
    }

    [Theory]
    [InlineData("app-self-test", "app-self-test (win-x64)")]
    [InlineData("app-sbom", "app-sbom (win-x64)")]
    public void What_needs_a_desktop_or_Trivy_stays_hosted_and_is_gated_by_its_own_plan_output(string id, string name)
    {
        var job = Job("ci.yml", id);
        Assert.Equal(name, Field(job, "name"));
        Assert.Equal("windows-latest", Field(job, "runs-on"));
        Assert.False(RunnerRouting.MayRunSelfHosted(job, id, "ci.yml"), $"{id} must never be able to take an ab-ci runner");
        Assert.Equal([ "plan", "app" ], Needs(job));
        // !cancelled(): app's matrix is red when only its waived Arm64 leg is, which must not skip the x64 SBOM publish and attest read.
        Assert.Equal($"!cancelled() && (needs.plan.outputs.{id} == 'true')", Field(job, "if"));
        Assert.Contains(id, Needs(Job("ci.yml", "quality-gate")));
    }

    [Fact]
    public void The_sbom_job_keeps_the_artifact_name_publish_and_attest_read()
    {
        var steps = Steps(Job("ci.yml", "app-sbom"));
        var upload = steps.Single(s => (Field(s, "uses") ?? "").StartsWith("actions/upload-artifact@", StringComparison.Ordinal));
        Assert.Equal("sbom-win-x64-${{ env.APP_VERSION }}", Field((YamlMappingNode)upload["with"], "name"));
        Assert.Equal("agent-bridge-windows-publish-${{ env.APP_VERSION }}", Field((YamlMappingNode)steps.Single(s => (Field(s, "uses") ?? "").StartsWith("actions/download-artifact@", StringComparison.Ordinal))["with"], "name"));
        Assert.Contains("agent-bridge-windows-win-x64-$env:APP_VERSION.cdx.json", string.Join('\n', steps.Select(s => Field(s, "run") ?? "")), StringComparison.Ordinal);
        var attest = string.Join('\n', Steps(Job("ci.yml", "attest")).Select(s => s.ToString()));
        Assert.Contains("sbom-*-${{ env.APP_VERSION }}", attest, StringComparison.Ordinal);
        Assert.Contains("agent-bridge-windows-win-x64-${{ env.APP_VERSION }}.cdx.json", attest, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pull_request_self_test_runs_the_composite_action_on_the_publish_folder_of_its_own_run()
    {
        var uses = Steps(Job("ci.yml", "app-self-test")).Single(s => Field(s, "uses") == "./.github/actions/app-self-test");
        Assert.Equal("agent-bridge-windows-publish-${{ env.APP_VERSION }}", Field((YamlMappingNode)uses["with"], "artifact"));
        Assert.Null(Field((YamlMappingNode)uses["with"], "run-id"));
    }

    [Fact]
    public void FULL_runs_the_self_test_after_every_merge_from_the_FAST_runs_publish_folder_under_the_name_the_release_train_requires()
    {
        var job = Job("full.yml", "app-self-test");
        Assert.Equal("app-selftest (win-x64)", Field(job, "name"));
        Assert.Equal("windows-latest", Field(job, "runs-on"));
        Assert.Equal(["trigger"], Needs(job));
        Assert.Equal("needs.trigger.outputs.ok == 'true'", Field(job, "if"));
        // The train reads this display name from FULL (build/select-release-commit.ps1); a rename would leave it waiting on a job that never appears.
        Assert.Contains("'app-selftest (win-x64)'", File.ReadAllText(Path.Combine(Root, "build", "select-release-commit.ps1")), StringComparison.Ordinal);
        var steps = Steps(job);
        var action = steps.Single(s => Field(s, "uses") == "./.github/actions/app-self-test");
        Assert.Equal("${{ needs.trigger.outputs.ci-run-id }}", Field((YamlMappingNode)action["with"], "run-id"));
        // The version is the FAST run's number, never this run's: FULL's own would count from 1, below every release.
        Assert.Contains("agent-bridge-windows-publish-0.1.$env:CI_RUN_NUMBER", string.Join('\n', steps.Select(s => Field(s, "run") ?? "")), StringComparison.Ordinal);
        Assert.Equal("${{ needs.trigger.outputs.tested }}", Field((YamlMappingNode)steps[0]["with"], "ref"));
        Assert.Contains("app-self-test", Needs(Job("full.yml", "full-gate")));
        var outputs = (YamlMappingNode)Job("full.yml", "trigger")["outputs"];
        Assert.NotNull(Field(outputs, "ci-run-id"));
        Assert.NotNull(Field(outputs, "ci-run-number"));
    }

    [Fact]
    public void A_run_of_FULL_by_hand_with_no_green_push_run_of_CI_on_the_commit_fails_rather_than_passes()
    {
        var script = Field(Steps(Job("full.yml", "trigger")).Single(s => Field(s, "id") == "commit"), "run")!;
        Assert.Contains("event=push&status=success", script, StringComparison.Ordinal);
        Assert.Contains("no green push run of CI on main", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_composite_action_runs_the_published_x64_app_from_the_downloaded_folder_and_uploads_its_diagnostics()
    {
        var text = File.ReadAllText(Path.Combine(Root, ".github", "actions", "app-self-test", "action.yml"));
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var steps = ((YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["runs"])["steps"]).Children.Cast<YamlMappingNode>().ToList();
        var download = steps.Single(s => (Field(s, "uses") ?? "").StartsWith("actions/download-artifact@", StringComparison.Ordinal));
        Assert.Equal("out/app/publish", Field((YamlMappingNode)download["with"], "path"));
        Assert.Equal("${{ inputs.run-id || github.run_id }}", Field((YamlMappingNode)download["with"], "run-id"));
        Assert.Contains("./build/app-self-test.ps1 -Exe out/app/publish/AgentBridge.exe", text, StringComparison.Ordinal);
        Assert.Contains("name: app-self-test-win-x64", text, StringComparison.Ordinal);
    }

    [Theory]
    // The app's own files, what it builds from and the self-test's own script: the plan selects app-package, so the job runs.
    [InlineData("src/AgentBridge.App/MainWindow.xaml.cs", true, true)]
    [InlineData("src/AgentBridge.App.Core/Cards.cs", true, true)]
    [InlineData("src/AgentBridge.Host.Core/SessionDrivers.cs", true, true)]
    [InlineData("build/app-self-test.ps1", true, true)]
    // The app's own unit tests: Quality's call (#4104), they exercise the app's code, so the pull request that changes them runs the self-test too.
    [InlineData("tests/AgentBridge.App.Tests/AppTimingsTests.cs", true, true)]
    // The host alone is packed and attested, but is not the app's own file: no desktop self-test on a pull request (main still runs it in FULL).
    [InlineData("src/AgentBridge.Host/Program.cs", false, true)]
    [InlineData("docs/status.md", false, false)]
    [InlineData("pwa/index.html", false, false)]
    public void A_pull_request_that_touches_the_app_runs_the_x64_self_test_and_one_that_does_not_skips_it(string changed, bool selfTest, bool sbom)
    {
        var selection = new Selector(Catalog.Load(Path.Combine(Root, "quality", "catalog.yml")), ProjectGraph.Load(Root)).Select([changed], ring: 1);
        var jobs = Program.JobsOf(selection);
        var ci = jobs.TryGetValue("ci.yml", out var found) ? found : new SortedDictionary<string, List<string>>();
        Assert.Equal(selfTest, ci.ContainsKey("app-self-test"));
        // The SBOM follows app itself: whenever the app is built, so is its SBOM (attest needs it).
        Assert.Equal(sbom, ci.ContainsKey("app-sbom"));
        Assert.Equal(sbom, ci.ContainsKey("app"));
    }

    [Fact]
    public void The_merge_rule_holds_main_on_the_self_test_and_the_catalog_lists_both_lanes_runs()
    {
        var config = MergeRuleConfig.Load(Path.Combine(Root, "quality", "merge-rule.yml"));
        Assert.Contains("app-self-test (win-x64)", config.MainGreen.Jobs);
        var catalog = Catalog.Load(Path.Combine(Root, "quality", "catalog.yml"));
        Assert.Contains("app-self-test", Completeness.ParseWhere(catalog.Suites.Single(s => s.Id == "app-package").Where!).Single(w => w.File == "ci.yml").Jobs);
        var full = catalog.Suites.Single(s => s.Id == "app-selftest-full");
        Assert.Equal("full", full.Lane);
        Assert.Equal("always", full.Select);
        Assert.Equal("full.yml app-self-test", full.Where);
    }
}
