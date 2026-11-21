using System.Diagnostics;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class PlanScriptTests
{
    [Theory]
    [InlineData("pull_request", ".github/workflows/merge-rule.yml", false, true)]
    [InlineData("pull_request", "quality/merge-rule.yml", false, true)]
    [InlineData("pull_request", ".github/workflows/ci.yml", true, true)]
    [InlineData("pull_request", "src/AgentBridge.App/View.cs", true, true)]
    [InlineData("pull_request", "build/pack-app.ps1", true, true)]
    [InlineData("pull_request", "Directory.Build.props", true, true)]
    [InlineData("pull_request", ".github/actions/install-trivy/action.yml", true, true)]
    [InlineData("push", "docs/guide.md", false, false)]
    [InlineData("push", "docs/guide.md", false, true)]
    [InlineData("push", ".github/workflows/merge-rule.yml", false, true)]
    [InlineData("push", ".github/CODEOWNERS", false, true)]
    [InlineData("push", "quality/merge-rule.yml", false, true)]
    [InlineData("push", ".github/workflows/ci.yml", true, true)]
    [InlineData("push", "src/AgentBridge.App/View.cs", true, false)]
    [InlineData("push", "src/AgentBridge.App/View.cs", true, true)]
    [InlineData("push", "build/pack-app.ps1", true, true)]
    [InlineData("push", "Directory.Build.props", true, true)]
    [InlineData("push", ".github/actions/install-trivy/action.yml", true, true)]
    [InlineData("push", "tests/Example.Tests/Test.cs", false, false)]
    [InlineData("push", "tests/Example.Tests/Shared.cs", true, false)]
    [InlineData("pull_request", "tests/Example.Tests/Test.cs", false, false)]
    [InlineData("push", "tests/AgentBridge.Quality.Tests/Test.cs", false, true)]
    [InlineData("push", "docs/protocol/examples/demo.json", true, false)]
    [InlineData("push", "unknown/input.txt", false, true)]
    [InlineData("workflow_dispatch", ".github/workflows/merge-rule.yml", true, true)]
    public Task The_workflows_plan_selects_the_installer_only_with_its_app_input(
        string eventName, string changed, bool packages, bool wholeRing) =>
        CheckPlan(eventName, changed, packages, wholeRing);

    [Fact]
    public Task A_test_and_code_change_cannot_skip_main_product() =>
        CheckPlan("push", "tests/Example.Tests/Test.cs", true, false, anotherFile: "src/AgentBridge.App/View.cs");

    [Theory]
    [InlineData("docs/guide.md")]
    [InlineData("tests/Example.Tests/Test.cs")]
    public Task A_green_but_unpublished_app_change_is_carried_by_the_next_main_head(string changed) =>
        CheckPlan("push", changed, true, false, unpaidApp: true);

    [Theory]
    [InlineData("windows-latest", true)]
    [InlineData("windows-11-arm", true)]
    [InlineData("ubuntu-latest", false)]
    public void Both_Windows_product_legs_use_the_same_bounded_retry_without_waiving_failure(string os, bool retry)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(File.ReadAllText(Path.Combine(ChangedFiles.RepoRoot(AppContext.BaseDirectory), ".github", "workflows", "ci.yml"))));
        var jobs = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["jobs"];
        var steps = ((YamlSequenceNode)((YamlMappingNode)jobs["product"])["steps"]).Children.Cast<YamlMappingNode>();
        var windows = steps.Single(s => s.Children.TryGetValue("run", out var run) && run.ToString().Contains("Invoke-ProductTestRetry", StringComparison.Ordinal));
        var coverage = steps.Single(s => s.Children.TryGetValue("name", out var name) && name.ToString() == "dotnet test, with coverage and per-test times");
        var context = new Dictionary<string, string?> { ["env.DOTNET_TARGET"] = "selected.slnf", ["matrix.os"] = os };
        Assert.Equal(retry, MergeRuleTests.IfExpression.Evaluate(windows["if"].ToString(), context));
        Assert.Equal(!retry, MergeRuleTests.IfExpression.Evaluate(coverage["if"].ToString(), context));
        Assert.Contains("exit (Invoke-ProductTestRetry", windows["run"].ToString(), StringComparison.Ordinal);
        Assert.False(windows.Children.ContainsKey("continue-on-error"));
    }

    [Theory]
    [InlineData("docs/guide.md", false)]
    [InlineData("docs/guide.md", true)]
    [InlineData(".github/workflows/merge-rule.yml", false)]
    [InlineData(".github/workflows/merge-rule.yml", true)]
    public Task A_push_without_a_proven_base_packages_an_earlier_untested_app_change(
        string changed, bool runsUnreadable) => CheckPlan("push", changed, true, true, runsUnreadable);

    static async Task CheckPlan(
        string eventName, string changed, bool packages, bool wholeRing, bool? runsUnreadable = null, string? anotherFile = null, bool unpaidApp = false)
    {
        var root = ChangedFiles.RepoRoot(AppContext.BaseDirectory);
        var folder = Fixture.ScratchFolder();
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))));
            var jobs = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode)["jobs"];
            var steps = (YamlSequenceNode)((YamlMappingNode)jobs["plan"])["steps"];
            var select = steps.Children.Cast<YamlMappingNode>().Single(step =>
                step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "select");
            var whole = steps.Children.Cast<YamlMappingNode>().Single(step =>
                step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "whole-ring");
            Write("quality/plan.ps1", File.ReadAllText(Path.Combine(root, "quality", "plan.ps1")));
            Write("src/AgentBridge.App/App.csproj", """<Project><ItemGroup><EmbeddedResource Include="../../docs/protocol/examples/*.json" /><Compile Include="../../tests/Example.Tests/Shared.cs" /></ItemGroup></Project>""");
            Write("tests/Example.Tests/Tests.csproj", """<Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""");
            Write("quality/catalog.yml", """
                global_triggers: [.github/workflows/**, .github/actions/**, quality/**, tests/AgentBridge.Quality.Tests/**, '**/Directory.Build.*']
                no_test_paths: [docs/*.md, .github/CODEOWNERS]
                suites:
                  - id: checks
                    validation_plan: ha-compatibility
                    kind: unit
                    ring: 1
                    select: always
                    triggers: [docs/protocol/**]
                    platforms: [linux-x64, win-x64, win-arm64]
                    runner: hosted
                    command: see ci.yml
                    where: ci.yml product, ha-integration, spikes, spikes-linux, light, pwa-ui, pwa-e2e
                    status: exists
                  - id: docs
                    validation_plan: native
                    kind: static
                    ring: 1
                    select: affected
                    platforms: [linux-x64]
                    runner: hosted
                    triggers: [docs/**]
                    command: see ci.yml light
                    where: ci.yml light
                    status: exists
                  - id: app
                    validation_plan: native
                    kind: packaging
                    ring: 1
                    select: affected
                    platforms: [win-x64]
                    runner: hosted
                    builds: src/AgentBridge.App
                    triggers: [build/**, docs/protocol/**]
                    command: see ci.yml app
                    where: ci.yml app
                    tier: post_merge
                    status: exists
                  - id: app-installer
                    validation_plan: native
                    kind: packaging
                    ring: 1
                    select: affected
                    platforms: [win-x64]
                    runner: hosted
                    builds: src/AgentBridge.App
                    triggers: [build/**, docs/protocol/**]
                    command: see ci.yml app-installer
                    where: ci.yml app-installer
                    tier: post-merge
                    tier_jobs: [app-installer (win-arm64)]
                    status: exists
                  - id: tests
                    validation_plan: native
                    kind: unit
                    ring: 1
                    select: affected
                    platforms: [linux-x64]
                    runner: hosted
                    project: tests/Example.Tests
                    command: see ci.yml product
                    where: ci.yml product
                    status: exists
                """);
            var tool = Path.Combine("tools", "AgentBridge.Quality", "bin", "Release", "net10.0");
            var target = Path.Combine(folder, tool);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, tool)))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));

            ChangedFiles.Git(folder, "init", "-q", "-b", "main");
            Commit("base", "quality/plan.ps1", "quality/catalog.yml", "src/AgentBridge.App/App.csproj", "tests/Example.Tests/Tests.csproj");
            ChangedFiles.Git(folder, "tag", "v0.1.1");
            if (runsUnreadable.HasValue || unpaidApp)
            {
                Write("src/AgentBridge.App/Earlier.cs", "earlier untested app change");
                Commit("untested app", "src/AgentBridge.App/Earlier.cs");
            }
            Write(changed, changed.EndsWith(".props", StringComparison.Ordinal) ? "<Project />" : "changed");
            Commit("change", changed);
            if (anotherFile is not null) { Write(anotherFile, "code"); Commit("code", anotherFile); }

            var output = Path.Combine(folder, "outputs");
            var start = new ProcessStartInfo("pwsh")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["CI_PLAN_TEST_RUN"] = select["run"].ToString();
            start.Environment["CI_PLAN_TEST_WHOLE"] = whole["run"].ToString();
            start.Environment["CI_PLAN_TEST_RUNS"] = unpaidApp ? "release-debt" : runsUnreadable.HasValue ? (runsUnreadable.Value ? "unreadable" : "none") : "";
            start.Environment["JOBS"] = ((YamlMappingNode)select["env"])["JOBS"].ToString();
            start.Environment["EVENT"] = eventName;
            start.Environment["DRAFT"] = "false";
            start.Environment["WHOLE_RING"] = wholeRing ? "true" : "false";
            start.Environment["BASE"] = anotherFile is null ? "HEAD^1" : "HEAD~2";
            start.Environment["BASE_KNOWN"] = "true";
            start.Environment["GITHUB_OUTPUT"] = output;
            start.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(folder, "summary");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         """
                         $ErrorActionPreference = 'Stop'
                         if ($env:CI_PLAN_TEST_RUNS) {
                             function global:gh {
                                 $global:LASTEXITCODE = if ($env:CI_PLAN_TEST_RUNS -eq 'unreadable') { 1 } else { 0 }
                                 if ($env:CI_PLAN_TEST_RUNS -eq 'release-debt') {
                                     if ($args[1] -like '*actions/workflows/*') { git rev-parse HEAD^1 }
                                     elseif ($args[1] -like '*releases?*') { 'v0.1.1' }
                                     else { throw "Unexpected offline gh call: $($args -join ' ')" }
                                 }
                             }
                             & ([scriptblock]::Create($env:CI_PLAN_TEST_WHOLE))
                             $whole = Get-Content $env:GITHUB_OUTPUT -Raw | ConvertFrom-StringData
                             $env:BASE = $whole['base']
                             $env:WHOLE_RING = $whole['whole-ring']
                             $env:BASE_KNOWN = $whole['base-known']
                             if ($env:CI_PLAN_TEST_RUNS -eq 'release-debt') {
                                 if ($env:BASE -ne (git rev-parse v0.1.1) -or $env:BASE_KNOWN -ne 'true') { throw 'A green CI must not erase unpublished app changes.' }
                             } elseif ($env:BASE -ne 'HEAD^1' -or $env:WHOLE_RING -ne 'true') { throw 'Unknown base must run the whole ring over the tip fallback.' }
                             Clear-Content $env:GITHUB_OUTPUT
                         }
                         & ([scriptblock]::Create($env:CI_PLAN_TEST_RUN))
                         """ })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start.");
            var error = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, (await error) + (await stdout));

            var outputs = File.ReadAllLines(output).Select(line => line.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            Assert.Equal(wholeRing ? "true" : "false", outputs["whole-solution"]);
            var expected = packages ? "true" : "false";
            Assert.Equal($"app={expected}\napp-installer={expected}",
                $"app={outputs["app"]}\napp-installer={outputs["app-installer"]}");
            foreach (var job in new[] { "product", "ha-integration", "spikes", "spikes-linux", "pwa-ui", "pwa-e2e" })
                Assert.Equal(job == "product" && eventName == "push" && changed.StartsWith("tests/", StringComparison.Ordinal)
                    && !wholeRing && anotherFile is null && !unpaidApp && changed != "tests/Example.Tests/Shared.cs" ? "false"
                    : wholeRing || unpaidApp || changed != "docs/guide.md" ? "true" : "false", outputs[job]);
            Assert.Equal("true", outputs["light"]);
            // An unknown tier (app's post_merge) plans as blocking; only exact post-merge names are listed.
            Assert.Equal(new Dictionary<string, string> { ["app"] = "blocking", ["app-installer"] = "blocking", ["app-installer (win-arm64)"] = "post-merge" },
                JsonSerializer.Deserialize<Dictionary<string, string>>(outputs["tiers"])!.Where(t => t.Key.StartsWith("app", StringComparison.Ordinal)).ToDictionary());
            Assert.Contains("| app-installer | " + (outputs["app-installer"] == "true" ? "yes" : "skipped") + " | blocking; post-merge: `app-installer (win-arm64)` |",
                File.ReadAllText(Path.Combine(folder, "summary")), StringComparison.Ordinal);
            if (wholeRing)
                Assert.Contains("Validation plans (reporting only): native=4, ha-compatibility=1.",
                    File.ReadAllText(Path.Combine(folder, "summary")), StringComparison.Ordinal);
            if (outputs["product"] == "true")
                Assert.Equal(eventName == "pull_request"
                        ? new[] { "ubuntu-latest", "windows-latest" }
                        : new[] { "ubuntu-latest", "windows-latest", "windows-11-arm" },
                    JsonSerializer.Deserialize<string[]>(outputs["product-os"]));
            Assert.Equal("needs.plan.outputs.app == 'true'", ((YamlMappingNode)jobs["app"])["if"].ToString());
            Assert.Equal("!cancelled() && github.event_name != 'pull_request' && (needs.plan.outputs.app-installer == 'true')",
                ((YamlMappingNode)jobs["app-installer"])["if"].ToString());
            Assert.Equal("${{ steps.whole-ring.outputs.base-known }}",
                ((YamlMappingNode)select["env"])["BASE_KNOWN"].ToString());
        }
        finally
        {
            Fixture.Delete(folder);
        }

        void Write(string path, string text)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        void Commit(string message, params string[] paths)
        {
            ChangedFiles.Git(folder, ["add", "--", .. paths]);
            ChangedFiles.Git(folder, "-c", "user.name=quality", "-c", "user.email=quality@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
        }
    }
}
