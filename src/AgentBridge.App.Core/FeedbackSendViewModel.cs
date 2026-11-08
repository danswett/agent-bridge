using System.Globalization;

namespace AgentBridge.App.Core;

public enum FeedbackSendState { Ready, Sending, Sent, Failed, TooLarge, Refused }

// The send half of the feedback review screen (#738 S4): sending, sent, and the ways it fails, each
// in plain words with Try again only where trying again can help. Nothing here ever carries report
// content or an exception message; a failure is a code the words are chosen from.
public sealed class FeedbackSendViewModel : Observable
{
    readonly IFeedbackSender sender;
    readonly Func<DateTimeOffset> now;
    readonly IUiDispatcher ui;
    CancellationTokenSource? running;
    FeedbackSendState state = FeedbackSendState.Ready;
    string message = "";
    Tone tone = Tone.None;
    bool canRetry;
    DateTimeOffset? retryAt;
    int attempts;

    public FeedbackSendViewModel(IFeedbackSender sender, Func<DateTimeOffset>? now = null, IUiDispatcher? ui = null)
    {
        this.sender = sender;
        this.now = now ?? (() => DateTimeOffset.Now);
        this.ui = ui ?? InlineDispatcher.Instance;
    }

    public FeedbackSendState State { get => state; private set => Set(ref state, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public Tone Tone { get => tone; private set => Set(ref tone, value); }
    public bool CanRetry { get => canRetry; private set => Set(ref canRetry, value); }
    public int Attempts => attempts;
    public bool IsSending => state == FeedbackSendState.Sending;
    // First send only; after a failure the person uses Try again, which sends the same bundle.
    public bool CanSend => state is FeedbackSendState.Ready;
    public string RetryLabel => "Try again";
    // The failure line is the one thing the "Report this" link sits beside.
    public bool Failed => state is FeedbackSendState.Failed or FeedbackSendState.TooLarge or FeedbackSendState.Refused;
    public FeedbackFailureCode? FailureCode { get; private set; }

    // What a screen reader says when the line changes.
    public string Speech => Message;

    // Whether Try again is pressable at `at`: after the relay's retry hint has passed.
    public bool RetryReady(DateTimeOffset at) => canRetry && (retryAt is not { } t || at >= t);

    public Task SendAsync() => Run();

    public Task RetryAsync() => CanRetry && RetryReady(now()) ? Run() : Task.CompletedTask;

    public void Cancel() => running?.Cancel();

    async Task Run()
    {
        if (state == FeedbackSendState.Sending || state == FeedbackSendState.Sent) { return; }
        var cancel = new CancellationTokenSource();
        running = cancel;
        attempts++;
        Apply(FeedbackSendState.Sending, "Sending…", Tone.Progress, false, null, null);
        FeedbackSendResult result;
        try { result = await sender.SendAsync(cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            ui.Post(() => Apply(FeedbackSendState.Ready, "", Tone.None, false, null, null));
            return;
        }
        catch
        {
            result = new FeedbackFailed(FeedbackFailureCode.Unexpected);
        }
        finally { if (ReferenceEquals(running, cancel)) { running = null; } cancel.Dispose(); }
        ui.Post(() => Settle(result));
    }

    void Settle(FeedbackSendResult result)
    {
        switch (result)
        {
            case FeedbackSent s:
                Apply(FeedbackSendState.Sent, $"Thanks. Sent at {s.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.", Tone.Ok, false, null, null);
                break;
            case FeedbackFailed f:
                var (text, retryable) = Describe(f);
                Apply(FeedbackSendState.Failed, text, Tone.Bad, retryable, f.RetryAfter is { } after ? now() + after : null, f.Code);
                break;
            case FeedbackTooLarge t:
                Apply(FeedbackSendState.TooLarge, $"Not sent: the report is {Words.Size(t.Bytes)}, over the {Words.Size(t.LimitBytes)} limit. Turn off the screenshot or the session turns and send again.", Tone.Bad, false, null, null);
                break;
            case FeedbackRefused:
                Apply(FeedbackSendState.Refused, "Not sent: the feedback service declined this report. Nothing was kept.", Tone.Bad, false, null, null);
                break;
            default:
                Apply(FeedbackSendState.Failed, "Couldn't send it. Try again.", Tone.Bad, true, null, FeedbackFailureCode.Unexpected);
                break;
        }
    }

    static (string Text, bool Retryable) Describe(FeedbackFailed f) => f.Code switch
    {
        FeedbackFailureCode.Offline => ("Not sent: this computer is offline. Check the connection, then try again.", true),
        FeedbackFailureCode.Timeout => ("Not sent: the feedback service took too long to answer. Try again.", true),
        FeedbackFailureCode.RelayBusy => ($"Not sent: the feedback service is busy.{RetryHint(f.RetryAfter)}", true),
        FeedbackFailureCode.RelayError => ("Not sent: the feedback service had a problem. Try again in a little while.", true),
        _ => ("Couldn't send it. Try again.", true),
    };

    static string RetryHint(TimeSpan? after) => after is { } a && a > TimeSpan.Zero
        ? $" Try again in {(a.TotalSeconds < 90 ? $"{Math.Ceiling(a.TotalSeconds)} seconds" : $"{Math.Ceiling(a.TotalMinutes)} minutes")}."
        : " Try again in a moment.";

    void Apply(FeedbackSendState s, string text, Tone t, bool retry, DateTimeOffset? at, FeedbackFailureCode? code)
    {
        retryAt = at;
        FailureCode = code;
        Message = text;
        Tone = t;
        CanRetry = retry;
        State = s;
        Raise(nameof(CanSend)); Raise(nameof(IsSending)); Raise(nameof(Failed)); Raise(nameof(Speech)); Raise(nameof(FailureCode)); Raise(nameof(Attempts));
    }
}
