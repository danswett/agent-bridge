using System.Globalization;
using AgentBridge.App.Core.Feedback;

namespace AgentBridge.App.Core;

public enum FeedbackSendState { Gathering, Ready, Sending, Sent, Failed, TooLarge, Refused }

// The send half of the feedback review screen (#738 S4): gathering, sending, sent, and the ways it
// fails, each in plain words with Try again only where the sender says trying again can help. Nothing
// here carries report content or an exception message; a failure is a reason the words are chosen from.
// The view model holds the gathered bundle and passes it on every send and retry.
public sealed class FeedbackSendViewModel : Observable
{
    // Product's words (#738): no beta queue, so offline and every upload failure read the same.
    public const string FailedText = "Couldn't send. Try again when you're online.";
    public const string GatheringText = "Gathering what's included…";
    public const string SendingText = "Sending…";

    readonly IFeedbackSender sender;
    readonly IUiDispatcher ui;
    readonly Func<DateTimeOffset> now;
    readonly Action<string, bool>? announce;
    readonly object gate = new();
    CancellationTokenSource? running;
    FeedbackBundle? bundle;
    FeedbackSendState state = FeedbackSendState.Gathering;
    string message = GatheringText;
    Tone tone = Tone.Progress;
    FeedbackRetry? retry;
    DateTimeOffset? retryAt;
    bool closed;
    // Bumped on every send, edit and close, so a result that arrives late is recognised and dropped.
    int generation;
    int attempts;

    // announce(speech, urgent) is the live-region hook, like Receipt's: urgent for a failure or refusal
    // (said assertively), polite otherwise. It is called on the UI for each change of the line.
    public FeedbackSendViewModel(IFeedbackSender sender, IUiDispatcher? ui = null, Func<DateTimeOffset>? now = null, Action<string, bool>? announce = null)
    {
        this.sender = sender;
        this.ui = ui ?? InlineDispatcher.Instance;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.announce = announce;
    }

    public FeedbackSendState State { get => state; private set => Set(ref state, value); }
    public string Message { get => message; private set => Set(ref message, value); }
    public Tone Tone { get => tone; private set => Set(ref tone, value); }
    public int Attempts => attempts;
    public bool IsSending => state == FeedbackSendState.Sending;
    public bool IsGathering => state == FeedbackSendState.Gathering;
    public bool IsClosed => closed;
    public string RetryLabel => "Try again";

    // First send only, once a bundle is gathered; after a failure the person uses Try again, and after
    // too large or refused they change the report, which gathers again (BeginGathering, then Ready).
    public bool CanSend => !closed && bundle is not null && state == FeedbackSendState.Ready;

    // What the sender said Try again does: Never hides it; Now and WhenOnline allow it; AfterDelay allows
    // it once RetryAt has passed. Nothing retries by itself; call RefreshRetry from a timer to update it.
    public bool CanRetry => !closed && bundle is not null && state == FeedbackSendState.Failed && retry switch
    {
        FeedbackRetry.Now or FeedbackRetry.WhenOnline => true,
        FeedbackRetry.AfterDelay => retryAt is null || now() >= retryAt,
        _ => false,
    };

    // Set for AfterDelay only; the screen shows "You can try again at {time}" from RetryHint.
    public DateTimeOffset? RetryAt => retryAt;
    public string RetryHint => state == FeedbackSendState.Failed && retry == FeedbackRetry.AfterDelay && retryAt is { } at && now() < at
        ? $"You can try again at {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}."
        : "";

    // The failure line is the one thing the "Report this" link sits beside.
    public bool Failed => state is FeedbackSendState.Failed or FeedbackSendState.TooLarge or FeedbackSendState.Refused;
    public FeedbackFailureReason? FailureReason { get; private set; }

    // What a screen reader says when the line changes.
    public string Speech => Message;

    // What the person typed and the reviewed preview, held in memory only so Try again sends the same
    // report; there is no beta queue (#738), so nothing is written to disk and Close clears it.
    public FeedbackDraft Draft { get; } = new();

    public void RefreshRetry() { Raise(nameof(CanRetry)); Raise(nameof(RetryHint)); }

    // The screen is gathering (first open, or after the person changed the report).
    public void BeginGathering()
    {
        lock (gate)
        {
            if (closed || state is FeedbackSendState.Sending or FeedbackSendState.Sent) { return; }
            generation++;
            bundle = null;
        }
        Apply(FeedbackSendState.Gathering, GatheringText, Tone.Progress, null, null, null, false);
    }

    // Gathering finished: the bundle the person reviewed is what every send and retry passes on.
    public void Ready(FeedbackBundle gathered)
    {
        lock (gate)
        {
            if (closed || state is FeedbackSendState.Sending or FeedbackSendState.Sent) { return; }
            generation++;
            bundle = gathered;
        }
        Apply(FeedbackSendState.Ready, "", Tone.None, null, null, null, false);
    }

    // Gathering ended without a bundle. Nothing can be sent until the report changes and gathers again.
    public void GatherRefused(FeedbackRefusalReason reason)
    {
        if (!DropBundle()) { return; }
        if (reason == FeedbackRefusalReason.BundleTooLarge)
        {
            Apply(FeedbackSendState.TooLarge, $"Not sent: the report is over the {Words.Size(FeedbackLimits.MaxBundleBytes)} limit. Turn off the screenshot or the session turns.", Tone.Bad, null, null, null, true);
            return;
        }
        var text = reason switch
        {
            FeedbackRefusalReason.NoteTooLong => $"Your note is over {FeedbackLimits.MaxNoteChars} characters. Shorten it.",
            FeedbackRefusalReason.ScreenshotTooLarge or FeedbackRefusalReason.ScreenshotNotPng => "The screenshot can't be attached. Remove it and try again.",
            _ => "That session's turns couldn't be read. Turn them off and try again.",
        };
        Apply(FeedbackSendState.Refused, text, Tone.Bad, null, FeedbackFailureReason.BadRequest, null, true);
    }

    public void GatherFailed()
    {
        if (!DropBundle()) { return; }
        Apply(FeedbackSendState.Failed, "Couldn't gather what's included. Change the report to try again.", Tone.Bad, FeedbackRetry.Never, FeedbackFailureReason.Unavailable, null, true);
    }

    bool DropBundle()
    {
        lock (gate)
        {
            if (closed || state is FeedbackSendState.Sending or FeedbackSendState.Sent) { return false; }
            generation++;
            bundle = null;
            return true;
        }
    }

    public void Close()
    {
        CancellationTokenSource? cancel;
        lock (gate)
        {
            if (closed) { return; }
            closed = true;
            generation++;
            bundle = null;
            cancel = running;
            running = null;
        }
        Cancel(cancel);
        Draft.Clear();
        Apply(FeedbackSendState.Ready, "", Tone.None, null, null, null, false);
    }

    public Task SendAsync() => CanSend ? Run() : Task.CompletedTask;

    public Task RetryAsync() => CanRetry ? Run() : Task.CompletedTask;

    public void Cancel()
    {
        CancellationTokenSource? cancel;
        lock (gate) { cancel = running; }
        Cancel(cancel);
    }

    static void Cancel(CancellationTokenSource? cancel)
    {
        try { cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    async Task Run()
    {
        FeedbackBundle send;
        CancellationTokenSource cancel;
        int mine;
        lock (gate)
        {
            if (closed || bundle is null || state == FeedbackSendState.Sending) { return; }
            send = bundle;
            cancel = running = new CancellationTokenSource();
            mine = ++generation;
            attempts++;
        }
        Apply(FeedbackSendState.Sending, SendingText, Tone.Progress, null, null, null, false);
        FeedbackSendResult? result;
        try { result = await sender.SendAsync(send, cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { result = null; }
        catch { result = new FeedbackSendResult.Failed(FeedbackFailureReason.UploadRejected, FeedbackRetry.WhenOnline); }
        bool current;
        lock (gate)
        {
            current = !closed && generation == mine;
            if (ReferenceEquals(running, cancel)) { running = null; }
        }
        if (!current) { return; }
        ui.Post(() => { if (Still(mine)) { Settle(result, cancel.IsCancellationRequested); } });
    }

    bool Still(int mine)
    {
        lock (gate) { return !closed && generation == mine; }
    }

    // A person's cancel returns to Ready without an error, unless the report had already been sent.
    void Settle(FeedbackSendResult? result, bool cancelled)
    {
        switch (result)
        {
            case FeedbackSendResult.Sent s:
                Apply(FeedbackSendState.Sent, $"Thanks. Sent at {s.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.", Tone.Ok, null, null, null, false);
                break;
            case null:
            case FeedbackSendResult.Failed { Reason: FeedbackFailureReason.Cancelled }:
                Apply(FeedbackSendState.Ready, "", Tone.None, null, null, null, false);
                break;
            case var _ when cancelled:
                Apply(FeedbackSendState.Ready, "", Tone.None, null, null, null, false);
                break;
            case FeedbackSendResult.Failed f:
                Apply(FeedbackSendState.Failed, FailedText, Tone.Bad, f.Retry, f.Reason, f.After is { } after ? now() + after : null, true);
                break;
            case FeedbackSendResult.TooLarge t:
                Apply(FeedbackSendState.TooLarge, $"Not sent: the report is {Words.Size(t.Bytes)}, over the {Words.Size(t.Max)} limit. Turn off the screenshot or the session turns and send again.", Tone.Bad, null, null, null, true);
                break;
            case FeedbackSendResult.Refused:
                Apply(FeedbackSendState.Refused, "Not sent: the feedback service declined this report. Nothing was kept.", Tone.Bad, null, null, null, true);
                break;
            default:
                Apply(FeedbackSendState.Failed, FailedText, Tone.Bad, FeedbackRetry.WhenOnline, FeedbackFailureReason.UploadRejected, null, true);
                break;
        }
    }

    void Apply(FeedbackSendState s, string text, Tone t, FeedbackRetry? retryKind, FeedbackFailureReason? reason, DateTimeOffset? at, bool urgent)
    {
        retry = retryKind;
        retryAt = at;
        FailureReason = reason;
        Message = text;
        Tone = t;
        State = s;
        Raise(nameof(CanSend)); Raise(nameof(CanRetry)); Raise(nameof(RetryAt)); Raise(nameof(RetryHint));
        Raise(nameof(IsSending)); Raise(nameof(IsGathering)); Raise(nameof(IsClosed));
        Raise(nameof(Failed)); Raise(nameof(Speech)); Raise(nameof(FailureReason)); Raise(nameof(Attempts));
        if (text.Length > 0 && !closed) { announce?.Invoke(text, urgent); }
    }
}

// In memory for the life of the open window and never persisted.
public sealed class FeedbackDraft
{
    public string TypedText { get; set; } = "";
    public string ReviewedPreview { get; set; } = "";
    public void Clear() { TypedText = ""; ReviewedPreview = ""; }
}