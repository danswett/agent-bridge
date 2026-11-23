namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class PlanScriptInstallerTests3
{
    [Theory]
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
        PlanScriptTests.CheckPlan(eventName, changed, packages, wholeRing);
}
