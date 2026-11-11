using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

internal enum RetryReason { Auto, Owner }
internal enum RetryStop { None, Changed, Ineligible, Expired, Exhausted, Cancelled, Failed }
internal sealed record RetryObservation(JsonElement Error, DateTimeOffset ErrorAt, SessionProcess Process, long ActivityVersion, bool CanReply);
internal sealed record RetryAttempt(int Number, DateTimeOffset At, RetryReason Reason, AnswerOutcome? Outcome);

internal sealed class RetryEpisode
{
    static readonly int[] DueSeconds = [30, 90, 180];
    readonly object sync = new();
    readonly Guid session;
    readonly Func<RetryObservation?> observe;
    readonly ISessionReplier replier;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly RetryObservation? initial;
    readonly long started;
    readonly TimeSpan age;
    readonly List<RetryAttempt> attempts = [];
    readonly HashSet<Guid> requests = [];
    RetryStop stop;
    bool sending;

    public RetryEpisode(Guid session, Func<RetryObservation?> observe, ISessionReplier replier, TimeProvider time, Action<string> log)
    {
        if (session == Guid.Empty) throw new ArgumentException("Session must be nonempty.", nameof(session));
        this.session = session;
        this.observe = observe;
        this.replier = replier;
        this.time = time;
        this.log = log;
        initial = observe();
        started = time.GetTimestamp();
        age = initial is null ? TimeSpan.Zero : time.GetUtcNow() - initial.ErrorAt;
        if (initial is not null && (!initial.CanReply || initial.Process.Id <= 0 || initial.Process.StartedAt is null || !CopilotTurnErrors.CanRetry(initial.Error)))
            End(RetryStop.Ineligible);
        else Check();
    }

    public int NextAttempt { get { lock (sync) return attempts.Count + 1; } }
    public RetryStop Stop { get { lock (sync) return stop; } }
    public IReadOnlyList<RetryAttempt> Attempts { get { lock (sync) return attempts.ToArray(); } }
    public void Cancel() { lock (sync) End(RetryStop.Cancelled); }

    public async Task<bool> TryAsync(int expectedAttempt, Guid request, RetryReason reason, CancellationToken cancellation = default)
    {
        if (request == Guid.Empty) throw new ArgumentException("Request must be nonempty.", nameof(request));
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (cancellation.IsCancellationRequested) { Cancel(); cancellation.ThrowIfCancellationRequested(); }
        int index;
        lock (sync)
        {
            if (sending || !Check() || stop != RetryStop.None) return false;
            if (cancellation.IsCancellationRequested)
            {
                End(RetryStop.Cancelled);
                cancellation.ThrowIfCancellationRequested();
            }
            if (expectedAttempt != attempts.Count + 1 || requests.Contains(request)) return false;
            if (reason == RetryReason.Auto && Elapsed.TotalSeconds < DueSeconds[attempts.Count]) return false;
            index = attempts.Count;
            requests.Add(request);
            attempts.Add(new(index + 1, time.GetUtcNow(), reason, null));
            sending = true;
        }
        try
        {
            log($"retry attempt {index + 1} {reason}: started");
            var result = await replier.ReplyAsync(new ReplyRequest
            {
                Id = session.ToString("D"), Request = request.ToString("D"), Text = "continue", RequireIdle = true,
            }, cancellationToken: cancellation).ConfigureAwait(false);
            lock (sync)
            {
                attempts[index] = attempts[index] with { Outcome = result.Outcome };
                if (cancellation.IsCancellationRequested) End(RetryStop.Cancelled);
                // Unconfirmed might already have typed: never retry an uncertain delivery.
                if (result.Outcome != AnswerOutcome.Delivered) End(RetryStop.Failed);
                else if (attempts.Count == DueSeconds.Length) End(RetryStop.Exhausted);
            }
            log($"retry attempt {index + 1} {reason}: {result.Outcome}");
            return true;
        }
        catch (OperationCanceledException)
        {
            Cancel();
            throw;
        }
        catch (Exception)
        {
            lock (sync) End(RetryStop.Failed);
            throw;
        }
        finally { lock (sync) sending = false; }
    }

    TimeSpan Elapsed => age + time.GetElapsedTime(started);

    bool Check()
    {
        if (stop != RetryStop.None) return false;
        if (age < TimeSpan.Zero) return End(RetryStop.Ineligible);
        if (Elapsed >= TimeSpan.FromMinutes(5)) return End(RetryStop.Expired);
        var current = observe();
        if (initial is null || current is null || current.Process != initial.Process || current.ActivityVersion != initial.ActivityVersion)
            return End(RetryStop.Changed);
        if (!current.CanReply || !replier.Available || !CopilotTurnErrors.CanRetry(current.Error))
            return End(RetryStop.Ineligible);
        if (Elapsed >= TimeSpan.FromMinutes(5)) return End(RetryStop.Expired);
        return true;
    }

    bool End(RetryStop reason)
    {
        if (stop == RetryStop.None)
        {
            stop = reason;
            log($"retry episode stopped: {reason}, attempts {attempts.Count}");
        }
        return false;
    }
}
