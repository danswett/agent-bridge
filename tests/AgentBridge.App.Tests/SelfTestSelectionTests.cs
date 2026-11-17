using Xunit;

namespace AgentBridge.App.Tests
{
    public sealed class SelfTestSelectionTests
    {
        [Theory]
        [InlineData("history", false)]
        [InlineData("wheel", true)]
        [InlineData("HISTORY", true)]
        public async Task A_group_selects_only_its_runner_regardless_of_argument_order(string group, bool groupFirst)
        {
            string[] selfTest = ["--self-test", "result.json"];
            string[] selector = ["--self-test-group", group];
            var options = AppOptions.Parse(groupFirst ? [.. selector, .. selfTest] : [.. selfTest, .. selector]);
            Assert.True(options.Demo);
            Assert.Equal(Path.GetFullPath("result.json"), options.SelfTest);
            Assert.Equal(group.ToLowerInvariant(), await SelectedRunner(options));
        }

        [Theory]
        [InlineData("--self-test", "full")]
        [InlineData("--self-test-history-scroll", "wheel")]
        [InlineData("--self-test-live-list", "live-list")]
        public async Task Without_a_group_each_existing_flag_keeps_its_runner(string flag, string expected) =>
            Assert.Equal(expected, await SelectedRunner(AppOptions.Parse([flag, "result.json"])));

        [Fact]
        public async Task Existing_live_list_precedence_is_unchanged() =>
            Assert.Equal("live-list", await SelectedRunner(AppOptions.Parse(
                ["--self-test-live-list", "live.json", "--self-test-history-scroll", "history.json"])));

        [Theory]
        [InlineData("--self-test-group")]
        [InlineData("--self-test-group", "unknown", "--self-test", "result.json")]
        [InlineData("--self-test-group", "", "--self-test", "result.json")]
        [InlineData("--self-test-group", "history")]
        [InlineData("--self-test-history-scroll", "result.json", "--self-test-group", "history")]
        [InlineData("--self-test-group", "wheel", "--self-test-live-list", "result.json")]
        [InlineData("--self-test", "result.json", "--self-test-group", "history", "--self-test-group", "wheel")]
        public void Invalid_or_ambiguous_groups_are_refused(params string[] args) =>
            Assert.Throws<ArgumentException>(() => AppOptions.Parse(args));

        static async Task<string> SelectedRunner(AppOptions options)
        {
            var calls = new List<string>();
            Func<Task> Runner(string name) => () => { calls.Add(name); return Task.CompletedTask; };
            // Reflection lets the tests-first head compile before the new dispatch seam exists.
            var dispatch = typeof(AppOptions).GetMethod("RunSelfTestAsync");
            Assert.NotNull(dispatch);
            await (Task)dispatch.Invoke(options,
                [Runner("full"), Runner("history"), Runner("wheel"), Runner("live-list")])!;
            return Assert.Single(calls);
        }
    }
}

// AppOptions shares a file with QueueDispatcher. No WinUI dispatcher is loaded by these tests.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => throw new NotSupportedException();
        public bool TryEnqueue(Action action) => throw new NotSupportedException();
    }
}
