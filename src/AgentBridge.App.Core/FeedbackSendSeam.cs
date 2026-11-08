namespace AgentBridge.App.Core;

// #738 S4 codes against these shapes until S1 (bundle) and S2 (seal and upload) land; the names are
// the ones the handoff fixed on #1185. Swap this file for the real types when they merge.
public abstract record FeedbackSendResult
{
    private protected FeedbackSendResult() { }
}

public sealed record FeedbackSent(DateTimeOffset At) : FeedbackSendResult;

// Why a send did not go through, as a code the UI words; never text from the report.
public enum FeedbackFailureCode { Offline, Timeout, RelayBusy, RelayError, Unexpected }

public sealed record FeedbackFailed(FeedbackFailureCode Code, TimeSpan? RetryAfter = null) : FeedbackSendResult;

public sealed record FeedbackTooLarge(long Bytes, long LimitBytes) : FeedbackSendResult;

public sealed record FeedbackRefused(string Code) : FeedbackSendResult;

// Sends the one bundle that was previewed. A retry sends that same bundle again, not a new gather.
public interface IFeedbackSender
{
    Task<FeedbackSendResult> SendAsync(CancellationToken cancel);
}
