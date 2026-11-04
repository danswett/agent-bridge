using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("UiTestFramework", "AgentBridge.App.Tests")]

// Every test here runs on a UI thread of its own (TestUi), from its class's constructor to its
// DisposeAsync, as the app's view models run on its one: a test's own steps, every action its
// dispatchers post, and whatever any of them awaits, one at a time. Without it a view model's
// await came back on whichever thread finished the call, beside the next posted action, which
// the app's UI thread never allows; that was the "rare timing race under load" of #101.
// The xunit runners are stock but for the one step that runs a test case.
public sealed class UiTestFramework(IMessageSink sink) : XunitTestFramework(sink)
{
    // The pool these tests run on has threads that wait by design. xunit's adapter and
    // vstest's message loop each hold one for the whole run. A dispatcher's Post from a pipe's
    // thread waits for its test's UI turn, as LockedDispatcher always has. The pool starts with
    // one thread per core and adds about one every half second once all of them are taken. On
    // a 4-core runner, a few tests side by side took all four: a timer's continuation then
    // waited 8.4 s with 11 threads and 33 items queued, and a connection to no host stayed
    // Connecting past its test's wait (#465, #511). Sampled every 50 ms on 4 pinned cores with
    // 4 busy loops, an item queued to the pool waited over a second 423 times in 6 runs before
    // #511, and once in 10 runs after.
    static UiTestFramework()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), io);
    }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider source, IMessageSink sink)
        : XunitTestFrameworkExecutor(assemblyName, source, sink)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new AssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    sealed class AssemblyRunner(ITestAssembly assembly, IEnumerable<IXunitTestCase> testCases, IMessageSink sink, IMessageSink executionSink, ITestFrameworkExecutionOptions options)
        : XunitTestAssemblyRunner(assembly, testCases, sink, executionSink, options)
    {
        protected override Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection, IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource) =>
            new CollectionRunner(testCollection, testCases, DiagnosticMessageSink, messageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), cancellationTokenSource).RunAsync();
    }

    sealed class CollectionRunner(ITestCollection collection, IEnumerable<IXunitTestCase> testCases, IMessageSink sink, IMessageBus bus, ITestCaseOrderer orderer, ExceptionAggregator aggregator, CancellationTokenSource cancel)
        : XunitTestCollectionRunner(collection, testCases, sink, bus, orderer, aggregator, cancel)
    {
        protected override Task<RunSummary> RunTestClassAsync(ITestClass testClass, IReflectionTypeInfo @class, IEnumerable<IXunitTestCase> testCases) =>
            new ClassRunner(testClass, @class, testCases, DiagnosticMessageSink, MessageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), CancellationTokenSource, CollectionFixtureMappings).RunAsync();
    }

    sealed class ClassRunner(ITestClass testClass, IReflectionTypeInfo @class, IEnumerable<IXunitTestCase> testCases, IMessageSink sink, IMessageBus bus, ITestCaseOrderer orderer, ExceptionAggregator aggregator, CancellationTokenSource cancel, IDictionary<Type, object> fixtures)
        : XunitTestClassRunner(testClass, @class, testCases, sink, bus, orderer, aggregator, cancel, fixtures)
    {
        protected override Task<RunSummary> RunTestMethodAsync(ITestMethod testMethod, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, object[] constructorArguments) =>
            new MethodRunner(testMethod, Class, method, testCases, DiagnosticMessageSink, MessageBus, new ExceptionAggregator(Aggregator), CancellationTokenSource, constructorArguments).RunAsync();
    }

    sealed class MethodRunner(ITestMethod testMethod, IReflectionTypeInfo @class, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, IMessageSink sink, IMessageBus bus, ExceptionAggregator aggregator, CancellationTokenSource cancel, object[] constructorArguments)
        : XunitTestMethodRunner(testMethod, @class, method, testCases, sink, bus, aggregator, cancel, constructorArguments)
    {
        protected override Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase) => TestUi.RunAsync(() => base.RunTestCaseAsync(testCase));
    }
}
