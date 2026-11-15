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

    [Fact]
    public void AgentCallerIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.Agent, readOnly: false));
    }

    [Fact]
    public void OtherCallerIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.Other, readOnly: false));
    }

    [Fact]
    public void HostCliCallerIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.HostCli, readOnly: false));
    }

    [Fact]
    public void UnknownCallerIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.Unknown, readOnly: false));
    }

    [Fact]
    public void ReadOnlySessionIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Copilot, EnrollmentCaller.App, readOnly: true));
    }

    [Fact]
    public void UnknownAgentKindIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, AgentKind.Unknown, EnrollmentCaller.App, readOnly: false));
    }

    [Fact]
    public void UnsupportedAgentKindIsRefused()
    {
        Assert.False(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, (AgentKind)int.MaxValue, EnrollmentCaller.App, readOnly: false));
    }

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public void OtherSupportedAgentKindsAreAllowed(AgentKind sessionKind)
    {
        Assert.True(LocalPersonEligibility.CanEnroll(
            EnrollmentOrigin.Local, sessionKind, EnrollmentCaller.App, readOnly: false));
    }
}
