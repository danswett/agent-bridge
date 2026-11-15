using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

public sealed class LocalPersonEligibilityTests
{
    [Fact]
    public void ExplicitLocalAppEnrollmentIsAllowed()
    {
        Assert.True(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.App, readOnly: false));
    }

    [Fact]
    public void UnknownOriginIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Unknown, AgentKind.Copilot, EnrollmentCaller.App, readOnly: false));
    }

    [Fact]
    public void RemoteOriginIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Remote, AgentKind.Copilot, EnrollmentCaller.App, readOnly: false));
    }

    [Theory]
    [InlineData(EnrollmentCaller.Agent)]
    [InlineData(EnrollmentCaller.Other)]
    [InlineData(EnrollmentCaller.HostCli)]
    [InlineData(EnrollmentCaller.Unknown)]
    public void NonAppCallerIsRefused(EnrollmentCaller caller)
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, caller, readOnly: false));
    }

    [Fact]
    public void ReadOnlySessionIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.App, readOnly: true));
    }

    [Theory]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Unknown)]
    public void EveryAgentKindIsExplicitlyRefusedByTheInertEntry(AgentKind sessionKind)
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, sessionKind, EnrollmentCaller.App, readOnly: false));
    }
}
