using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class UpdateVersionSkewTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 17, 22, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0.2.0", "0.2.0+build123", UpdateOutcome.Updated)]
    [InlineData("0.2.0", "0.2.0+build123", UpdateOutcome.Current)]
    [InlineData("0.2.0+build123", "0.2.0", UpdateOutcome.Updated)]
    [InlineData("0.2.0+build123", "0.2.0", UpdateOutcome.Current)]
    [InlineData("0.2.0-RC.1", "0.2.0-rc.1", UpdateOutcome.Updated)]
    [InlineData("0.2.0-RC.1", "0.2.0-rc.1", UpdateOutcome.Current)]
    [InlineData("0.0.0", "0.2.0", UpdateOutcome.Updated)]
    [InlineData("0.0.0", "0.2.0", UpdateOutcome.Current)]
    public void Equivalent_release_identities_and_the_unknown_host_sentinel_do_not_invent_skew(
        string hostVersion, string appVersion, UpdateOutcome outcome)
    {
        var updates = new UpdateService(hostVersion);
        using var app = new AppChannel();
        var report = Report(appVersion, outcome);

        updates.Report(app, report);

        Assert.Equal(report.State, updates.State());
    }

    [Theory]
    [InlineData("0.1.0", "0.2.0", UpdateOutcome.Updated)]
    [InlineData("0.2.0", "0.1.0", UpdateOutcome.Updated)]
    [InlineData("0.1.0", "0.2.0", UpdateOutcome.Current)]
    [InlineData("0.2.0", "0.1.0", UpdateOutcome.Current)]
    public void App_and_host_version_skew_is_reported_instead_of_a_successful_machine_update(
        string hostVersion, string appVersion, UpdateOutcome claimed)
    {
        var updates = new UpdateService(hostVersion);
        using var app = new AppChannel();
        var report = Report(appVersion, claimed);

        updates.Report(app, report);
        var state = updates.State();

        var last = Assert.IsType<UpdateResult>(state.Last);
        Assert.Equal(UpdateOutcome.Failed, last.Outcome);
        Assert.Contains(hostVersion, last.Reason);
        Assert.Contains(appVersion, last.Reason);
        Assert.Equal("version-skew", last.Code);
        Assert.Equal("host-version", last.Step);
        Assert.Equal(report.State.Last!.Request, last.Request);
        Assert.Equal(report.State.Last.From, last.From);
        Assert.Equal(report.State.Last.To, last.To);
        Assert.Equal(report.State.Last.At, last.At);
        Assert.True(state.Installer);
        Assert.Equal(appVersion, state.Current);
        Assert.Equal("0.3.0", state.Available);
        Assert.Equal(claimed, report.State.Last!.Outcome);
    }

    [Theory]
    [InlineData(UpdateOutcome.Updated)]
    [InlineData(UpdateOutcome.Current)]
    public void A_report_is_forwarded_unchanged_once_app_and_host_have_converged(UpdateOutcome outcome)
    {
        var updates = new UpdateService("0.2.0");
        using var app = new AppChannel();
        updates.Report(app, Report("0.2.0", outcome));
        updates.Report(app, Report("0.1.0", outcome));
        var converged = Report("0.2.0", outcome);

        updates.Report(app, converged);

        Assert.Equal(converged.State, updates.State());
    }

    [Theory]
    [InlineData(UpdateOutcome.Failed)]
    [InlineData(UpdateOutcome.Refused)]
    public void Skew_does_not_replace_an_existing_failure_or_refusal(UpdateOutcome outcome)
    {
        var updates = new UpdateService("0.1.0");
        using var app = new AppChannel();
        var report = Report("0.2.0", outcome);

        updates.Report(app, report);

        Assert.Equal(report.State, updates.State());
    }

    [Theory]
    [InlineData("0.1.0", null, true)]
    [InlineData("0.1.0", "", true)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("", "0.2.0", true)]
    [InlineData("0.0.0+build123", "0.2.0", true)]
    [InlineData("0.2.0", "0.0.0", true)]
    public void Unknown_or_uninstalled_versions_do_not_invent_skew(string hostVersion, string? appVersion, bool installed)
    {
        var updates = new UpdateService(hostVersion);
        using var app = new AppChannel();
        var report = new UpdateReport(Report("0.2.0", UpdateOutcome.Current).State with
        {
            Current = appVersion, Installer = installed,
        }, null);

        updates.Report(app, report);

        Assert.Equal(report.State, updates.State());
    }

    static UpdateReport Report(string version, UpdateOutcome outcome) => new(new UpdateState
    {
        Current = version,
        Installer = true,
        Available = "0.3.0",
        Downloaded = true,
        Last = new UpdateResult
        {
            Outcome = outcome, From = "0.0.1", To = version, At = Now,
            Request = "66666666-6666-4666-8666-666666666666",
        },
    }, null);

    sealed class AppChannel : IProtocolChannel, IDisposable
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PeerId => "local-app";
        public string PeerName => "app";
        public string PeerKind => "local";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
        public void Dispose() => closed.TrySetResult();
    }
}
