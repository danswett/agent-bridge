using System.ComponentModel;

namespace AgentBridge.App.Core;

public enum UpdateApplyEnd { Unknown, Completed, Failed }

// WorkersFinished covers the actual apply and any delegated worker, not just its launcher.
// ActiveVersion must be read freshly from the active installation, not a cached locator.
public sealed record UpdateApplyEvidence(bool? WorkersFinished, int? ApplyExitCode, string? ActiveVersion);

public sealed record UpdateApplyResult(UpdateApplyEnd End, string? Code, string Step,
    string TargetVersion, int? ApplyExitCode, string? ActiveVersion, DateTimeOffset At, TimeSpan Elapsed,
    string? ErrorType = null, int? ErrorCode = null);

public sealed class UpdateApplyCompletion(
    Func<CancellationToken, Task<UpdateApplyEvidence>> observe, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    public async Task<UpdateApplyResult> RunAsync(string targetVersion, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Poll, TimeSpan.Zero);
        cancel.ThrowIfCancellationRequested();
        var began = clock.GetTimestamp();
        UpdateApplyEvidence? last = null;
        using var deadline = new CancellationTokenSource(Window, clock);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel, deadline.Token);

        UpdateApplyResult End(UpdateApplyEnd end, string? code, string step = "apply-completion",
            Exception? error = null) =>
            new(end, code, step, targetVersion, last?.ApplyExitCode, last?.ActiveVersion,
                clock.GetUtcNow(), clock.GetElapsedTime(began), error?.GetType().Name,
                error is Win32Exception native ? native.NativeErrorCode : error?.HResult);

        try
        {
            while (true)
            {
                waiting.Token.ThrowIfCancellationRequested();
                last = await observe(waiting.Token).WaitAsync(waiting.Token).ConfigureAwait(false);
                waiting.Token.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(began) >= Window)
                    return End(UpdateApplyEnd.Unknown, "timeout");
                if (last.WorkersFinished == true)
                {
                    if (last.ApplyExitCode is null)
                        return End(UpdateApplyEnd.Unknown, "exit-unknown");
                    if (last.ApplyExitCode != 0)
                        return End(UpdateApplyEnd.Failed, "exit-failed");
                    if (string.IsNullOrWhiteSpace(last.ActiveVersion))
                        return End(UpdateApplyEnd.Unknown, "version-unknown", "active-version");
                    if (!string.Equals(last.ActiveVersion, targetVersion, StringComparison.Ordinal))
                        return End(UpdateApplyEnd.Unknown, "version-skew", "active-version");
                    return End(UpdateApplyEnd.Completed, null);
                }
                await Task.Delay(Poll, clock, waiting.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return End(UpdateApplyEnd.Unknown, "timeout");
        }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            return End(UpdateApplyEnd.Unknown, "probe-cancelled", error: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            cancel.ThrowIfCancellationRequested();
            return End(UpdateApplyEnd.Unknown, deadline.IsCancellationRequested ? "timeout" : "probe-unreadable",
                error: ex);
        }
    }
}
