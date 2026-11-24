using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace AgentBridge.App.Core;

public sealed class UpdateCoordinator(
    Func<CancellationToken, Task> waitForApp,
    Func<Guid, CancellationToken, Task<UpdateApplyEvidence>> observeApply,
    Func<IDisposable> acquirePackageLock,
    Func<string?> readActiveVersion,
    Action respawn,
    TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public Task RespawnFinished { get; private set; } = Task.CompletedTask;
    public Task ObservationFinished { get; private set; } = Task.CompletedTask;

    public async Task<UpdateApplyResult> RunAsync(string targetVersion, CancellationToken cancel = default)
    {
        Task? appExit = null;
        var custody = new LockCustody();
        var step = "app-exit";
        var began = clock.GetTimestamp();
        async Task<UpdateApplyEvidence> Observe(Guid generation, CancellationToken ct)
        {
            appExit ??= waitForApp(ct);
            await appExit.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            step = "apply-completion";
            var evidence = await observeApply(generation, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (evidence.Generation != generation || evidence.WorkersFinished != true || evidence.ApplyExitCode != 0)
                return evidence with { ActiveVersion = null };
            step = "package-lock";
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            custody.Take(acquirePackageLock(), ct);
            ct.ThrowIfCancellationRequested();
            step = "active-version";
            var active = readActiveVersion();
            ct.ThrowIfCancellationRequested();
            return evidence with { ActiveVersion = active };
        }
        var completion = new UpdateApplyCompletion((generation, ct) =>
        {
            var work = Observe(generation, ct);
            ObservationFinished = work;
            return work;
        }, clock);

        UpdateApplyResult Failure(UpdateApplyResult previous, string failedStep, Exception error) =>
            previous with
            {
                End = UpdateApplyEnd.Unknown, Code = "coordinator-failed", Step = failedStep,
                At = clock.GetUtcNow(), Elapsed = clock.GetElapsedTime(began),
                ErrorType = error.GetType().Name,
                ErrorCode = error is Win32Exception native ? native.NativeErrorCode : error.HResult
            };

        UpdateApplyResult result = new(UpdateApplyEnd.Unknown, null, step, targetVersion,
            null, null, clock.GetUtcNow(), TimeSpan.Zero);
        ExceptionDispatchInfo? cancellation = null;
        Exception? releaseError = null;
        try
        {
            result = await completion.RunAsync(targetVersion, cancel).ConfigureAwait(false);
            result = result with { Step = step };
            if (result.End == UpdateApplyEnd.Completed)
            {
                cancel.ThrowIfCancellationRequested();
                var remaining = completion.Window - clock.GetElapsedTime(began);
                if (remaining <= TimeSpan.Zero)
                    result = result with { End = UpdateApplyEnd.Unknown, Code = "timeout" };
                else
                {
                    using var deadline = new CancellationTokenSource(remaining, clock);
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);
                    var token = waiting.Token;
                    try
                    {
                        RespawnFinished = Task.Run(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            step = "respawn";
                            respawn();
                        });
                        await RespawnFinished.WaitAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (clock.GetElapsedTime(began) >= completion.Window)
                            result = result with { End = UpdateApplyEnd.Unknown, Code = "timeout" };
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested && deadline.IsCancellationRequested)
                    {
                        result = result with { End = UpdateApplyEnd.Unknown, Code = "timeout" };
                    }
                    catch (Exception error)
                    {
                        cancel.ThrowIfCancellationRequested();
                        result = Failure(result, step, error);
                    }
                }
                result = result with { Step = step, At = clock.GetUtcNow(), Elapsed = clock.GetElapsedTime(began) };
            }
        }
        catch (OperationCanceledException error)
        {
            cancellation = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            try { custody.Dispose(); }
            catch (Exception error)
            {
                releaseError = error;
            }
        }
        if (releaseError is not null) return Failure(result, "package-unlock", releaseError);
        cancellation?.Throw();
        return result;
    }

    // A timed-out probe may still return from a synchronous lock acquisition. Never leak its lease.
    sealed class LockCustody : IDisposable
    {
        readonly object gate = new();
        IDisposable? held;
        bool closed;

        public void Take(IDisposable acquired, CancellationToken cancel)
        {
            ArgumentNullException.ThrowIfNull(acquired);
            lock (gate)
            {
                if (!closed && !cancel.IsCancellationRequested)
                {
                    held = acquired;
                    return;
                }
            }
            acquired.Dispose();
            cancel.ThrowIfCancellationRequested();
            throw new InvalidOperationException("package lock arrived after observation ended");
        }

        public void Dispose()
        {
            IDisposable? release;
            lock (gate)
            {
                closed = true;
                release = held;
                held = null;
            }
            release?.Dispose();
        }
    }
}
