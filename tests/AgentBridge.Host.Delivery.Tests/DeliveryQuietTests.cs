using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryQuietTests
{
    [Fact]
    public void A_committed_exclusion_cannot_be_released_even_through_IDisposable()
    {
        var gate = new DeliveryGate(new FakeTimeProvider());
        using var exclusion = gate.TryExclude(TimeSpan.FromSeconds(30), out _)!;
        Assert.True(exclusion.TryBeginCommit(out _));
        Assert.True(exclusion.TryCommit(new DepartureConnection(), 0, false, 0, out _));
        exclusion.Dispose();
        ((IDisposable)exclusion).Dispose();
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task An_accepted_request_is_fenced_before_its_background_worker_claims_a_session()
    {
        var gate = new DeliveryGate();
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new OutcomeMemory<int>(TimeProvider.System, TimeSpan.FromMinutes(15), gate, () => -1);
        var key = Guid.NewGuid();
        var request = memory.Once(key, () => finish.Task, _ => { });
        Assert.Null(gate.TryExclude(TimeSpan.FromSeconds(30), out var why));
        Assert.Equal(DeliveryGate.Blocker.DeliveryInFlight, why);
        finish.SetResult(7);
        Assert.Equal(7, await request);
        using var quiet = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        Assert.NotNull(quiet);
        Assert.Equal(7, await memory.Once(key, () => throw new Exception("must not replay"), _ => { }));
        var next = Guid.NewGuid();
        Assert.Equal(-1, await memory.Once(next, () => Task.FromResult(8), _ => { }));
        quiet.Dispose();
        Assert.Equal(8, await memory.Once(next, () => Task.FromResult(8), _ => { }));
    }

    [Fact]
    public async Task An_accepted_outcome_is_complete_before_its_admission_is_released()
    {
        var gate = new DeliveryGate();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedAtRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var memory = new OutcomeMemory<int>(TimeProvider.System, TimeSpan.FromMinutes(15), gate, () => -1);
        Task<int>? request = null;
        gate.AdmissionReleased = () => completedAtRelease.TrySetResult(request!.IsCompleted);
        request = memory.Once(Guid.NewGuid(), () => release.Task, _ => { });
        release.SetResult(7);
        Assert.True(await completedAtRelease.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(7, await request.WaitAsync(TimeSpan.FromSeconds(2)));
        using var fence = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        Assert.NotNull(fence);
    }

    [Fact]
    public async Task A_typing_worker_cannot_be_interrupted_by_a_quiet_grant()
    {
        var gate = new DeliveryGate();
        using var delivery = gate.TryEnterDelivery();
        await gate.WaitToTypeAsync();
        try
        {
            Assert.Null(gate.TryExclude(TimeSpan.FromSeconds(30), out var why));
            Assert.Equal(DeliveryGate.Blocker.Typing, why);
        }
        finally { gate.DoneTyping(); }
    }

    [Fact]
    public void An_expired_provisional_fence_releases_but_its_stale_owner_cannot_release_a_new_one()
    {
        var time = new FakeTimeProvider();
        var gate = new DeliveryGate(time);
        using var first = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnterDelivery());
        time.Advance(TimeSpan.FromSeconds(30));
        using (var admitted = gate.TryEnterDelivery()) { Assert.NotNull(admitted); }
        using var second = gate.TryExclude(TimeSpan.FromSeconds(30), out _);
        first.Dispose();
        Assert.Null(gate.TryEnterDelivery());
        second!.Dispose();
        using var next = gate.TryEnterDelivery();
        Assert.NotNull(next);
    }
}
