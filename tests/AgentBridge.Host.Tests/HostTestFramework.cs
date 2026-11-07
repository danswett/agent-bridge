using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("HostTestFramework", "AgentBridge.Host.Tests")]

// Keep xunit's stock collection admission and runners; only observe lifecycle messages.
public sealed class HostTestFramework(IMessageSink sink) : XunitTestFramework(sink)
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

    sealed class AssemblyRunner(ITestAssembly assembly, IEnumerable<IXunitTestCase> testCases, IMessageSink sink,
        IMessageSink executionSink, ITestFrameworkExecutionOptions options)
        : XunitTestAssemblyRunner(assembly, testCases, sink, executionSink, options)
    {
        readonly TestCollectionActivity activity = new();

        protected override IMessageBus CreateMessageBus() => new TestCollectionMessageBus(base.CreateMessageBus(), activity);
    }
}
