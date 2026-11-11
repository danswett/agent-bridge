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
    [InlineData("workflow_dispatch", ".github/workflows/merge-rule.yml", true, true)]
    public async Task The_workflows_plan_selects_the_installer_only_with_its_app_input(
        string eventName, string changed, bool packages, bool wholeRing)
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
            Write("quality/plan.ps1", File.ReadAllText(Path.Combine(root, "quality", "plan.ps1")));
            Write("src/AgentBridge.App/App.csproj", "<Project />");
            Write("quality/catalog.yml", """
                global_triggers: [.github/workflows/**, .github/actions/**, quality/**, '**/Directory.Build.*']
                no_test_paths: [docs/**, .github/CODEOWNERS]
                suites:
                  - id: checks
                    kind: unit
                    ring: 1
                    select: always
                    platforms: [linux-x64, win-x64, win-arm64]
                    runner: hosted
                    command: see ci.yml
                    where: ci.yml product, ha-integration, spikes, spikes-linux, light, pwa-ui, pwa-e2e
                    status: exists
                  - id: docs
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
                    kind: packaging
                    ring: 1
                    select: affected
                    platforms: [win-x64]
                    runner: hosted
                    builds: src/AgentBridge.App
                    triggers: [build/**]
                    command: see ci.yml app
                    where: ci.yml app
                    status: exists
                  - id: app-installer
                    kind: packaging
                    ring: 1
                    select: affected
                    platforms: [win-x64]
                    runner: hosted
                    builds: src/AgentBridge.App
                    triggers: [build/**]
                    command: see ci.yml app-installer
                    where: ci.yml app-installer
                    status: exists
                """);
            var tool = Path.Combine("tools", "AgentBridge.Quality", "bin", "Release", "net10.0");
            var target = Path.Combine(folder, tool);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, tool)))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));

            ChangedFiles.Git(folder, "init", "-q", "-b", "main");
            Commit("base", "quality/plan.ps1", "quality/catalog.yml", "src/AgentBridge.App/App.csproj");
            Write(changed, changed.EndsWith(".props", StringComparison.Ordinal) ? "<Project />" : "changed");
            Commit("change", changed);

            var output = Path.Combine(folder, "outputs");
            var start = new ProcessStartInfo("pwsh")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["CI_PLAN_TEST_RUN"] = select["run"].ToString();
            start.Environment["JOBS"] = ((YamlMappingNode)select["env"])["JOBS"].ToString();
            start.Environment["EVENT"] = eventName;
            start.Environment["DRAFT"] = "false";
            start.Environment["WHOLE_RING"] = wholeRing ? "true" : "false";
            start.Environment["BASE"] = "HEAD^1";
            start.Environment["GITHUB_OUTPUT"] = output;
            start.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(folder, "summary");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
                         "$ErrorActionPreference = 'Stop'; & ([scriptblock]::Create($env:CI_PLAN_TEST_RUN))" })
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
                Assert.Equal(wholeRing || !changed.StartsWith("docs/", StringComparison.Ordinal) ? "true" : "false", outputs[job]);
            Assert.Equal("true", outputs["light"]);
            if (outputs["product"] == "true")
                Assert.Equal(eventName == "pull_request"
                        ? new[] { "ubuntu-latest", "windows-latest" }
                        : new[] { "ubuntu-latest", "windows-latest", "windows-11-arm" },
                    JsonSerializer.Deserialize<string[]>(outputs["product-os"]));
            Assert.Equal("needs.plan.outputs.app == 'true'", ((YamlMappingNode)jobs["app"])["if"].ToString());
            Assert.Equal("!cancelled() && (needs.plan.outputs.app-installer == 'true')",
                ((YamlMappingNode)jobs["app-installer"])["if"].ToString());
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
