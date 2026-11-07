using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

// The tests' UI thread itself (TestUi, UiTestFramework): what every other test here relies on to
// see the view models as the app's one UI thread leaves them (#101).
public sealed class UiThreadTests
{
    readonly LockedDispatcher ui = new();

    [Fact]
    public async Task A_test_runs_on_its_UI_thread_before_and_after_each_await()
    {
        Assert.True(TestUi.ForTest.OnIt);
        await Task.Delay(1);
        Assert.True(TestUi.ForTest.OnIt);
        await Task.Run(() => Assert.False(TestUi.ForTest.OnIt));
        Assert.True(TestUi.ForTest.OnIt);
    }

    [Fact]
    public async Task What_an_action_posted_from_another_thread_awaits_comes_back_to_the_UI_thread()
    {
        var back = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Handle()
        {
            await Task.Delay(1);
            back.SetResult(TestUi.ForTest.OnIt);
        }
        // As a host event arrives: on the pipe's thread, posted to the UI.
        Task? handled = null;
        await Task.Run(() => ui.Post(() => handled = Handle()));
        await handled!.WaitAsync(FakeHost.Patience);
        Assert.True(await back.Task);
    }

    [Fact]
    public async Task Nothing_posted_runs_while_a_test_step_is_part_way_through()
    {
        using var posting = new ManualResetEventSlim();
        using var ran = new ManualResetEventSlim();
        var post = Task.Run(() =>
        {
            posting.Set();
            ui.Post(ran.Set);
        });
        // The post has been made from the other thread; until this step awaits, it cannot run.
        Assert.True(posting.Wait(FakeHost.Patience), "the post");
        Assert.False(ran.Wait(TimeSpan.FromMilliseconds(100)));
        await post;
        Assert.True(ran.IsSet);
    }

    [Fact]
    public async Task Each_test_has_a_UI_thread_of_its_own()
    {
        var mine = TestUi.ForTest;
        var other = await Task.Run(() => TestUi.RunAsync(() => Task.FromResult(TestUi.ForTest)));
        Assert.NotSame(mine, other);
        Assert.True(mine.OnIt);
        Assert.False(other.OnIt);
    }

    [Fact]
    public async Task A_timed_out_UI_wait_reports_workers_running_collections_and_total_process_threads()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            Wait.UntilAsync(() => false, "diagnostic condition", TimeSpan.Zero, () => "still false"));

        var snapshot = Regex.Match(error.Message,
            @"^timed out waiting for diagnostic condition \([^)]*longest gap between looks \d+ ms; pool threads \d+, queued \d+; "
            + @"pool workers available (\d+), busy (\d+), max (\d+); active test collections [1-9]\d*; total process threads [1-9]\d*\): still false$");
        Assert.True(snapshot.Success, error.Message);
        Assert.Equal(int.Parse(snapshot.Groups[3].Value),
            int.Parse(snapshot.Groups[1].Value) + int.Parse(snapshot.Groups[2].Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Collections_wait_for_the_configured_number_of_UI_threads(int threads)
    {
        var assembly = new TestAssembly(Reflector.Wrap(typeof(UiThreadTests).Assembly));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CollectionStarts();
        var cases = Enumerable.Range(0, 4).Select(n => new WaitingCase(
            new TestCollection(assembly, null, $"collection {n}"), released.Task)).ToArray();
        var options = new ExecutionOptions();
        options.SetValue("xunit.execution.MaxParallelThreads", threads);
        options.SetValue("xunit.execution.SynchronousMessageReporting", true);
        using var runner = new CountingRunner(assembly, cases, sink, options, attempted);
        var run = runner.RunAsync();
        try
        {
            await attempted.Task.WaitAsync(FakeHost.Patience);
            var limit = threads == 0 ? Environment.ProcessorCount : threads;
            var expected = limit < 0 ? cases.Length : Math.Min(limit, cases.Length);
            Assert.Equal(expected, sink.Count);
            Assert.Equal(expected, runner.ActiveCollectionCount);
        }
        finally
        {
            released.TrySetResult();
            var summary = await run.WaitAsync(FakeHost.Patience);
            Assert.Equal(0, runner.ActiveCollectionCount);
            Assert.Equal(cases.Length, summary.Total);
            Assert.Equal(0, summary.Failed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_collections_unwind_without_counting_cancelled_waiters(bool cancelActive)
    {
        var assembly = new TestAssembly(Reflector.Wrap(typeof(UiThreadTests).Assembly));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CollectionStarts();
        var cases = Enumerable.Range(0, 2).Select(n => new WaitingCase(
            new TestCollection(assembly, null, $"collection {n}"), released.Task)).ToArray();
        var options = new ExecutionOptions();
        options.SetValue("xunit.execution.MaxParallelThreads", 1);
        using var runner = new CountingRunner(assembly, cases, sink, options, new TaskCompletionSource());
        using var bus = new SynchronousMessageBus(sink, false);
        using var activeCancel = new CancellationTokenSource();
        using var queuedCancel = new CancellationTokenSource();
        var active = runner.RunCollectionAsync(cases[0], bus, activeCancel);
        try
        {
            var queued = runner.RunCollectionAsync(cases[1], bus, queuedCancel);
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, runner.ActiveCollectionCount);
            queuedCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(FakeHost.Patience));
            Assert.Equal(1, runner.ActiveCollectionCount);
        }
        finally
        {
            if (cancelActive) { released.TrySetCanceled(); }
            else { released.TrySetException(new InvalidOperationException("synthetic collection failure")); }
        }

        if (cancelActive) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(FakeHost.Patience)); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => active.WaitAsync(FakeHost.Patience)); }
        Assert.Equal(0, runner.ActiveCollectionCount);
        var after = new WaitingCase(new TestCollection(assembly, null, "after unwind"), Task.CompletedTask);
        Assert.Equal(1, (await runner.RunCollectionAsync(after, bus, activeCancel).WaitAsync(FakeHost.Patience)).Total);
        Assert.Equal(0, runner.ActiveCollectionCount);
    }

    sealed class ExecutionOptions : ITestFrameworkExecutionOptions
    {
        readonly Dictionary<string, object?> values = new();
        public TValue GetValue<TValue>(string name) => values.TryGetValue(name, out var value) ? (TValue)value! : default!;
        public void SetValue<TValue>(string name, TValue value) => values[name] = value;
    }

    sealed class CollectionStarts : LongLivedMarshalByRefObject, IMessageSink
    {
        int count;
        public int Count => Volatile.Read(ref count);
        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is ITestCollectionStarting)
                Interlocked.Increment(ref count);
            return true;
        }
    }

    sealed class CountingRunner(ITestAssembly assembly, IXunitTestCase[] cases, IMessageSink sink,
        ITestFrameworkExecutionOptions options, TaskCompletionSource attempted)
        : UiTestFramework.AssemblyRunner(assembly, cases, sink, sink, options)
    {
        int count;
        readonly int total = cases.Length;

        public Task<RunSummary> RunCollectionAsync(WaitingCase testCase, IMessageBus bus, CancellationTokenSource cancel) =>
            base.RunTestCollectionAsync(bus, testCase.TestMethod.TestClass.TestCollection, [testCase], cancel);

        protected override Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection,
            IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            var run = base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
            if (Interlocked.Increment(ref count) == total)
                attempted.TrySetResult();
            return run;
        }
    }

    sealed class WaitingCase : XunitTestCase
    {
        readonly Task released = Task.CompletedTask;

        [Obsolete("Only for xunit's de-serializer; these cases are constructed in-process.", true)]
        public WaitingCase() { }

        public WaitingCase(ITestCollection collection, Task released)
            : base(new CollectionStarts(), TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.None,
                new TestMethod(new TestClass(collection, Reflector.Wrap(typeof(UiThreadTests))),
                    Reflector.Wrap(typeof(UiThreadTests).GetMethod(nameof(A_test_runs_on_its_UI_thread_before_and_after_each_await))!)))
        {
            this.released = released;
        }

        public override async Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
            object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        {
            Assert.True(TestUi.ForTest.OnIt);
            await released;
            Assert.True(TestUi.ForTest.OnIt);
            return new RunSummary { Total = 1 };
        }
    }
}
