namespace AgentBridge.Quality.Tests;

[Trait("Category", "Process")]
public class PlanScriptMainHeadTests
{
    [Fact]
    public Task A_test_and_code_change_cannot_skip_main_product() =>
        PlanScriptTests.CheckPlan("push", "tests/Example.Tests/Test.cs", true, false, anotherFile: "src/AgentBridge.App/View.cs");

    [Theory]
    [InlineData("docs/guide.md")]
    [InlineData("tests/Example.Tests/Test.cs")]
    public Task A_green_but_unpublished_app_change_is_carried_by_the_next_main_head(string changed) =>
        PlanScriptTests.CheckPlan("push", changed, true, false, unpaidApp: true);

    [Theory]
    [InlineData("docs/guide.md", false)]
    [InlineData("docs/guide.md", true)]
    [InlineData(".github/workflows/merge-rule.yml", false)]
    [InlineData(".github/workflows/merge-rule.yml", true)]
    public Task A_push_without_a_proven_base_packages_an_earlier_untested_app_change(
        string changed, bool runsUnreadable) => PlanScriptTests.CheckPlan("push", changed, true, true, runsUnreadable);
}
