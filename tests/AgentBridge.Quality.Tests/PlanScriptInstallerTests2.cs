namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class PlanScriptInstallerTests2
{
    [Theory]
    [InlineData("push", "docs/guide.md", false, true)]
    [InlineData("push", ".github/workflows/merge-rule.yml", false, true)]
    [InlineData("push", ".github/CODEOWNERS", false, true)]
    [InlineData("push", "quality/merge-rule.yml", false, true)]
    [InlineData("push", ".github/workflows/ci.yml", true, true)]
    [InlineData("push", "src/AgentBridge.App/View.cs", true, false)]
    [InlineData("push", "src/AgentBridge.App/View.cs", true, true)]
    [InlineData("push", "build/pack-app.ps1", true, true)]
    public Task The_workflows_plan_selects_the_installer_only_with_its_app_input(
        string eventName, string changed, bool packages, bool wholeRing) =>
        PlanScriptTests.CheckPlan(eventName, changed, packages, wholeRing);
}
