using System.Diagnostics;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class PlanScriptTests
{
    [Theory]
    [InlineData("pull_request", ".github/workflows/merge-rule.yml", false)]
    [InlineData("pull_request", ".github/workflows/ci.yml", true)]
    [InlineData("pull_request", "src/AgentBridge.App/View.cs", true)]
    [InlineData("pull_request", "build/pack-app.ps1", true)]
    [InlineData("pull_request", "Directory.Build.props", true)]
    [InlineData("pull_request", ".github/actions/install-trivy/action.yml", true)]
    [InlineData("push", ".github/workflows/merge-rule.yml", true)]
    [InlineData("workflow_dispatch", ".github/workflows/merge-rule.yml", true)]
    public async Task The_workflows_plan_selects_the_installer_only_with_its_app_input(string eventName, string changed, bool packages)
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
                global_triggers: [.github/workflows/**, '**/Directory.Build.*']
                suites:
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
            start.Environment["WHOLE_RING"] = "true";
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
            Assert.Equal("true", outputs["whole-solution"]);
            var expected = packages ? "true" : "false";
            Assert.Equal($"app={expected}\napp-installer={expected}",
                $"app={outputs["app"]}\napp-installer={outputs["app-installer"]}");
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
