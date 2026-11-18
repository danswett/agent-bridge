namespace AgentBridge.App.Core;

public sealed class UpdateApplyRetry(
    Func<CancellationToken, Task<UpdateApplyResult>> attempt, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public static async Task<T> BeforeCommitAsync<T>(Func<Action, Task<T>> apply,
        TimeProvider? time = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        var clock = time ?? TimeProvider.System;
        HashSet<int> failures = [];
        for (var number = 1; ; number++)
        {
            cancel.ThrowIfCancellationRequested();
            var committing = false;
            try
            {
                var result = await apply(() =>
                {
                    cancel.ThrowIfCancellationRequested();
                    committing = true;
                }).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                return result;
            }
            // This task must cover the entire attempt, including cleanup. Never retry across publication.
            catch (IOException error) when (!committing
                && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                cancel.ThrowIfCancellationRequested();
                if (!failures.Add(error.HResult) || number == 3) throw;
            }
            await Task.Delay(TimeSpan.FromSeconds(1 << (number - 1)), clock, cancel).ConfigureAwait(false);
        }
    }

    public async Task<UpdateApplyResult> RunAsync(CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        HashSet<(string Step, string? Code, int? Exit)> failures = [];
        for (var number = 1; ; number++)
        {
            cancel.ThrowIfCancellationRequested();
            var result = await attempt(cancel).ConfigureAwait(false);
            // Unknown may still own a worker; only positively finished failures permit another apply.
            if (result is not { End: UpdateApplyEnd.Failed, Code: "exit-failed",
                Step: "apply-completion", ApplyExitCode: not null and not 0, ErrorType: null, ErrorCode: null }
                || !failures.Add((result.Step, result.Code, result.ApplyExitCode)) || number == 3)
                return result;
            await Task.Delay(TimeSpan.FromSeconds(1 << (number - 1)), clock, cancel).ConfigureAwait(false);
        }
    }
}
