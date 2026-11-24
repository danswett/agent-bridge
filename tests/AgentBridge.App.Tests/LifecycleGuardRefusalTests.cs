using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// #4546: the real, parameterless LifecycleGuard.Refusal() is what Program and the install hooks call, and it reads AGENT_BRIDGE_SESSION itself. Every other test reaches the guard through a seam that is
// handed the session, so a Refusal() that passed a constant null, or returned null outright, would stay green. This one sets the variable and calls the real thing. It sets a process-wide environment
// variable, so it runs ALONE like RunCompletionTests, and keeps its coverage (QuarantineTests lists it).
[Collection(nameof(LifecycleGuardRefusalTests))]
public sealed class LifecycleGuardRefusalTests
{
    const string Variable = "AGENT_BRIDGE_SESSION";

    [Fact]
    public void The_real_Refusal_refuses_a_process_whose_environment_names_a_host_session()
    {
        var was = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, "lifecycle-guard-refusal-test");
        try
        {
            Assert.Contains($"({Variable} is set)", LifecycleGuard.Refusal(), StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(Variable, was); }
    }
}

[CollectionDefinition(nameof(LifecycleGuardRefusalTests), DisableParallelization = true)]
public sealed class LifecycleGuardRefusalCollection;
