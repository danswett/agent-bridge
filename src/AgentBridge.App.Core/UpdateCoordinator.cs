using System.ComponentModel;

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

    public async Task<UpdateApplyResult> RunAsync(string targetVersion, CancellationToken cancel = default)
    {
        Task? appExit = null;
        var custody = new LockCustody();
        var step = "app-exit";
        var began = clock.GetTimestamp();
        var completion = new UpdateApplyCompletion(async (generation, ct) =>
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
            custody.Take(acquirePackageLock(), ct);
            ct.ThrowIfCancellationRequested();
            step = "active-version";
            var active = readActiveVersion();
            ct.ThrowIfCancellationRequested();
            return evidence with { ActiveVersion = active };
        }, clock);

        UpdateApplyResult Failure(UpdateApplyResult previous, string failedStep, Exception error) =>
            previous with
            {
                End = UpdateApplyEnd.Unknown, Code = "coordinator-failed", Step = failedStep,
                At = clock.GetUtcNow(), Elapsed = clock.GetElapsedTime(began),
                ErrorType = error.GetType().Name,
                ErrorCode = error is Win32Exception native ? native.NativeErrorCode : error.HResult
            };

        UpdateApplyResult result;
        Exception? releaseError = null;
        try
        {
            result = await completion.RunAsync(targetVersion, cancel).ConfigureAwait(false);
            result = result with { Step = step };
            if (result.End == UpdateApplyEnd.Completed)
            {
                cancel.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(began) >= completion.Window)
                    result = result with { End = UpdateApplyEnd.Unknown, Code = "timeout", Step = "respawn" };
                else
                {
                    try
                    {
                        respawn();
                        result = result with { Step = "respawn", At = clock.GetUtcNow(), Elapsed = clock.GetElapsedTime(began) };
                    }
                    catch (Exception error)
                    {
                        cancel.ThrowIfCancellationRequested();
                        result = Failure(result, "respawn", error);
                    }
                }
            }
        }
        finally
        {
            try { custody.Dispose(); }
            catch (Exception error)
            {
                cancel.ThrowIfCancellationRequested();
                releaseError = error;
            }
        }
        return releaseError is null ? result : Failure(result, "package-unlock", releaseError);
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
