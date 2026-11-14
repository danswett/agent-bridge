using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class RetryEpisodeTests
{
    const string Empty = """{"errorType":"query","message":"No response was returned. Send your message again to retry."}""";

    [Fact]
    public async Task Repeated_empty_response_errors_keep_one_budget_and_three_timed_continues()
    {
        var h = new Harness();
        Assert.False(await h.Send());
        foreach (var seconds in new[] { 30, 60, 90 })
        {
            h.Time.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.False(await h.Send());
            h.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await h.Send());
            h.Current = h.Current! with { ErrorAt = h.Time.GetUtcNow() };
        }
        Assert.Equal(new[] { 30, 90, 180 }, h.Episode.Attempts.Select(a => (int)(a.At - h.Start).TotalSeconds));
        Assert.All(h.Requests, r => { Assert.Equal("continue", r.Text); Assert.True(r.RequireIdle); });
        Assert.Equal(3, h.Requests.Select(r => r.Request).Distinct().Count());
        Assert.Equal(RetryStop.Exhausted, h.Episode.Stop);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task An_owner_attempt_cancels_the_old_auto_slot_and_duplicate_requests_spend_nothing()
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await h.Send());
        var pendingAuto = h.Episode.NextAttempt;
        var request = Guid.NewGuid();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await h.Episode.TryAsync(pendingAuto, request, RetryReason.Owner));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await h.Episode.TryAsync(pendingAuto, Guid.NewGuid(), RetryReason.Auto));
        Assert.False(await h.Episode.TryAsync(h.Episode.NextAttempt, request, RetryReason.Owner));
        Assert.Equal(2, h.Requests.Count);
        h.Time.Advance(TimeSpan.FromSeconds(90));
        Assert.True(await h.Send());
    }

    [Fact]
    public async Task Owner_and_auto_cannot_both_send_while_one_attempt_is_in_flight()
    {
        var h = new Harness { Pending = new() };
        h.Time.Advance(TimeSpan.FromSeconds(30));
        var first = h.Send();
        Assert.False(first.IsCompleted);
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Single(h.Requests);
        h.Pending.SetResult(new(AnswerOutcome.Delivered));
        Assert.True(await first);
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("personReply")]
    [InlineData("restart")]
    [InlineData("gone")]
    [InlineData("notIdle")]
    [InlineData("healed")]
    [InlineData("question")]
    [InlineData("readOnly")]
    public async Task Fresh_ineligibility_or_movement_ends_the_episode_without_another_send(string change)
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await h.Send());
        h.Current = change switch
        {
            "activity" or "personReply" => h.Current! with { ActivityVersion = 1 },
            "restart" => h.Current! with { Process = new(8, h.Start.AddSeconds(1)) },
            "gone" => null,
            _ => h.Current! with { CanReply = false },
        };
        h.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(await h.Send());
        Assert.NotEqual(RetryStop.None, h.Episode.Stop);
        h.Current = h.Initial;
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Single(h.Requests);
    }

    [Theory]
    [InlineData("""{"errorType":"authorization","statusCode":401,"remediation":"sign_in"}""")]
    [InlineData("""{"errorType":"quota","message":"Quota exceeded"}""")]
    [InlineData("""{"errorType":"policy","message":"Not allowed"}""")]
    [InlineData("""{"errorType":"query","message":"unknown"}""")]
    [InlineData("""{"errorType":"query","message":"fetch failed"}""")]
    [InlineData("""{"errorType":"query","message":"session event delivery failed: session host did not complete the session.background_tasks_changed event within 120s"}""")]
    public async Task Non_allow_listed_errors_including_the_host_wedge_never_reach_the_replier(string error)
    {
        var h = new Harness(error);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await h.Send());
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Empty(h.Requests);
        Assert.Empty(h.Episode.Attempts);
    }

    [Fact]
    public async Task The_original_five_minute_deadline_and_cancellation_stop_even_an_owner_attempt()
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.Current = h.Current! with { ErrorAt = h.Time.GetUtcNow() };
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Equal(RetryStop.Expired, h.Episode.Stop);
        Assert.Empty(h.Requests);
        var cancelled = new Harness();
        cancelled.Episode.Cancel();
        cancelled.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await cancelled.Send());
        Assert.Empty(cancelled.Requests);
    }

    [Fact]
    public async Task Cancellation_keeps_the_in_flight_slot_until_delivery_finishes_and_forbids_another_attempt()
    {
        var h = new Harness { Pending = new() };
        using var cancel = new CancellationTokenSource();
        var first = h.Episode.TryAsync(1, Guid.NewGuid(), RetryReason.Owner, cancel.Token);
        cancel.Cancel();
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.False(first.IsCompleted);
        h.Pending.SetResult(new(AnswerOutcome.Delivered));
        Assert.True(await first);
        Assert.Equal(RetryStop.Cancelled, h.Episode.Stop);
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Initial_observation_time_counts_once_without_resetting_offsets_or_deadline()
    {
        var h = new Harness();
        var reads = 0;
        var late = new RetryEpisode(Guid.NewGuid(), () =>
        {
            if (reads++ == 0) h.Time.Advance(TimeSpan.FromSeconds(20));
            return h.Current;
        }, h, h.Time, h.Log.Add);
        Assert.False(await late.TryAsync(1, Guid.NewGuid(), RetryReason.Auto));
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await late.TryAsync(1, Guid.NewGuid(), RetryReason.Auto));
        h.Time.Advance(TimeSpan.FromSeconds(269));
        Assert.True(await late.TryAsync(2, Guid.NewGuid(), RetryReason.Owner));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await late.TryAsync(3, Guid.NewGuid(), RetryReason.Owner));
        Assert.Equal(RetryStop.Expired, late.Stop);
        Assert.Equal(2, h.Requests.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Slow_observation_or_availability_cannot_admit_an_owner_or_auto_after_expiry(bool availability, bool owner)
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromSeconds(299));
        h.ObservationDelay = availability ? TimeSpan.Zero : TimeSpan.FromSeconds(2);
        h.AvailabilityDelay = availability ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
        Assert.False(await h.Send(owner ? RetryReason.Owner : RetryReason.Auto));
        Assert.Equal(RetryStop.Expired, h.Episode.Stop);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_inside_observe_or_available_never_consumes_or_sends_an_attempt(bool availability, bool owner)
    {
        var h = new Harness();
        using var cancel = new CancellationTokenSource();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.OnObservation = availability ? null : cancel.Cancel;
        h.OnAvailability = availability ? cancel.Cancel : null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Episode.TryAsync(1, Guid.NewGuid(), owner ? RetryReason.Owner : RetryReason.Auto, cancel.Token));
        Assert.Equal(RetryStop.Cancelled, h.Episode.Stop);
        Assert.Empty(h.Episode.Attempts);
        Assert.Empty(h.Requests);
        Assert.False(await h.Send(RetryReason.Owner));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Episode_cancel_inside_observe_or_available_never_consumes_or_sends_an_attempt(bool availability, bool owner)
    {
        var h = new Harness();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.OnObservation = availability ? null : h.Episode.Cancel;
        h.OnAvailability = availability ? h.Episode.Cancel : null;
        Assert.False(await h.Send(owner ? RetryReason.Owner : RetryReason.Auto));
        Assert.Equal(RetryStop.Cancelled, h.Episode.Stop);
        Assert.Empty(h.Episode.Attempts);
        Assert.Empty(h.Requests);
        Assert.False(await h.Send(RetryReason.Owner));
    }

    [Fact]
    public async Task Read_only_delivery_and_a_new_non_transient_error_stop_before_sending()
    {
        var h = new Harness { Available = false };
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await h.Send());
        Assert.Empty(h.Requests);
        var changed = new Harness();
        Assert.True(await changed.Send(RetryReason.Owner));
        changed.Current = changed.Current! with { Error = JsonSerializer.SerializeToElement(new { errorType = "authorization" }) };
        changed.Time.Advance(TimeSpan.FromSeconds(90));
        Assert.False(await changed.Send());
        Assert.Single(changed.Requests);
    }

    [Fact]
    public async Task A_replier_throwing_cancellation_ends_the_episode_and_releases_no_new_attempt()
    {
        var h = new Harness { Failure = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Send(RetryReason.Owner));
        Assert.Equal(RetryStop.Cancelled, h.Episode.Stop);
        Assert.False(await h.Send(RetryReason.Owner));
        Assert.Single(h.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Observe_and_available_allow_another_thread_to_cancel_without_holding_the_episode_lock(bool availability, bool fence)
    {
        var h = new Harness { Pending = new() };
        var send = fence ? h.Send(RetryReason.Owner) : null;
        Action cancel = () => Assert.True(Task.Run(h.Episode.Cancel).Wait(TimeSpan.FromSeconds(10)));
        h.OnObservation = availability ? null : cancel;
        h.OnAvailability = availability ? cancel : null;
        if (fence)
        {
            Assert.False(Assert.Single(h.Requests).StillCurrent!());
            h.Pending.SetResult(new(AnswerOutcome.Delivered));
            Assert.True(await send!);
        }
        else { Assert.False(await h.Send(RetryReason.Owner)); }
        Assert.Equal(RetryStop.Cancelled, h.Episode.Stop);
    }

    [Fact]
    public async Task A_stale_delivery_records_changed_instead_of_generic_failure()
    {
        var h = new Harness { Outcome = new(AnswerOutcome.Stale) };
        Assert.True(await h.Send(RetryReason.Owner));
        Assert.Equal(AnswerOutcome.Stale, Assert.Single(h.Episode.Attempts).Outcome);
        Assert.Equal(RetryStop.Changed, h.Episode.Stop);
        Assert.False(await h.Send(RetryReason.Owner));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Concurrent_eligibility_readers_recheck_the_slot_and_in_flight_exclusion(bool availability, int next)
    {
        var h = new Harness { Pending = new() };
        h.Time.Advance(TimeSpan.FromSeconds(30));
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        TaskCompletionSource[] entered = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        var reads = 0;
        Action park = () =>
        {
            var index = Interlocked.Increment(ref reads) - 1;
            entered[index].SetResult();
            Assert.True((index == 0 ? releaseFirst : releaseSecond).Wait(TimeSpan.FromSeconds(10)));
        };
        h.OnObservation = availability ? null : park;
        h.OnAvailability = availability ? park : null;
        var first = Task.Run(() => h.Episode.TryAsync(1, Guid.NewGuid(), RetryReason.Auto));
        Task<bool>? second = null;
        try
        {
            await entered[0].Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = Task.Run(() => h.Episode.TryAsync(next, Guid.NewGuid(), RetryReason.Owner));
            await entered[1].Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseFirst.Set();
            await h.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseSecond.Set();
            Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(h.Episode.Attempts);
            Assert.Single(h.Requests);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            releaseFirst.Set(); releaseSecond.Set();
            h.Pending.TrySetResult(new(AnswerOutcome.Delivered));
            await first;
            if (second is not null) { await second; }
        }
    }

    [Fact]
    public async Task Outcomes_are_visible_but_replier_reasons_and_exception_text_never_enter_retry_records_or_logs()
    {
        var h = new Harness { Outcome = new(AnswerOutcome.Unconfirmed, "private-canary") };
        Assert.True(await h.Send(RetryReason.Owner));
        Assert.Equal(AnswerOutcome.Unconfirmed, Assert.Single(h.Episode.Attempts).Outcome);
        Assert.Equal(RetryStop.Failed, h.Episode.Stop);
        Assert.False(await h.Send(RetryReason.Owner));
        var failed = new Harness { Failure = new IOException("private-canary") };
        await Assert.ThrowsAsync<IOException>(() => failed.Send(RetryReason.Owner));
        Assert.Equal(RetryStop.Failed, failed.Episode.Stop);
        Assert.Single(failed.Episode.Attempts);
        Assert.DoesNotContain("private-canary", string.Join("\n", failed.Log));
        Assert.DoesNotContain("private-canary", string.Join("\n", h.Log));
        Assert.DoesNotContain("private-canary", JsonSerializer.Serialize(h.Episode.Attempts));
        Assert.False(await failed.Send(RetryReason.Owner));
    }

    sealed class Harness : ISessionReplier
    {
        public readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public DateTimeOffset Start => Initial.ErrorAt;
        public readonly RetryObservation Initial;
        public RetryObservation? Current;
        public readonly RetryEpisode Episode;
        public readonly List<ReplyRequest> Requests = [];
        public readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Log = [];
        bool available = true;
        public TimeSpan ObservationDelay, AvailabilityDelay;
        public Action? OnObservation, OnAvailability;
        public bool Available { get { OnAvailability?.Invoke(); Time.Advance(AvailabilityDelay); return available; } set => available = value; }
        public TaskCompletionSource<ReplyResult>? Pending;
        public ReplyResult Outcome = new(AnswerOutcome.Delivered);
        public Exception? Failure;
        public Harness(string error = Empty)
        {
            Initial = Current = new(JsonSerializer.Deserialize<JsonElement>(error), Time.GetUtcNow(), new(7, Time.GetUtcNow()), 0, true);
            Episode = new(Guid.NewGuid(), () => { OnObservation?.Invoke(); Time.Advance(ObservationDelay); return Current; }, this, Time, Log.Add);
        }
        public Task<bool> Send(RetryReason reason = RetryReason.Auto) =>
            Episode.TryAsync(Episode.NextAttempt, Guid.NewGuid(), reason);
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            Requests.Add(request);
            Sent.TrySetResult();
            return Failure is { } failure ? Task.FromException<ReplyResult>(failure) : Pending?.Task ?? Task.FromResult(Outcome);
        }
    }
}
