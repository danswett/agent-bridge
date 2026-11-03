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
