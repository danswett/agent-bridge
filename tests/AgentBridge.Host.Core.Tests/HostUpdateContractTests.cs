using AgentBridge.Host.Core.Updates;
using Xunit;

public sealed class HostUpdateContractTests
{
    [Fact]
    public async Task A_native_client_can_hand_the_checked_release_to_restart_without_an_HA_adapter()
    {
        var release = new UpdateRelease("0.2.0", "A fix.", null) { Handle = new object() };
        var updater = new NativeUpdates(release);
        IHostUpdates client = updater;

        var found = Assert.IsType<UpdateRelease>(await client.CheckAsync(CancellationToken.None));
        await client.DownloadAsync(found, _ => { }, CancellationToken.None);
        await client.ReadyAsync(CancellationToken.None);
        await client.ApplyAndRestartAsync(found);

        Assert.Same(release, updater.Downloaded);
        Assert.Same(release, updater.Applied);
        Assert.Same(release.Handle, updater.Applied!.Handle);
    }

    [Fact]
    public async Task A_native_client_observes_a_failed_restart_handover()
    {
        var release = new UpdateRelease("0.2.0", null, null);
        var failure = new InvalidOperationException("The update could not be handed over.");
        var updater = new NativeUpdates(release) { Failure = failure };
        IHostUpdates client = updater;

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ApplyAndRestartAsync(release));

        Assert.Same(failure, observed);
        Assert.Null(updater.Applied);
    }

    sealed class NativeUpdates(UpdateRelease release) : IHostUpdates
    {
        public string? Installed => "0.1.0";
        public string? ReleasesUrl => null;
        public UpdateRelease? Downloaded { get; private set; }
        public UpdateRelease? Applied { get; private set; }
        public Exception? Failure { get; init; }

        public Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<UpdateRelease?>(release);

        public Task DownloadAsync(UpdateRelease found, Action<int> progress, CancellationToken cancellationToken)
        {
            Downloaded = found;
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(UpdateRelease found)
        {
            if (Failure is { } failure) { throw failure; }
            Applied = found;
        }
    }
}
