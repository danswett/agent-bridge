using System.Globalization;

namespace AgentBridge.App.Core;

public enum FeedbackSendState { Ready, Sending, Sent, Failed, TooLarge, Refused }

// The send half of the feedback review screen (#738 S4): sending, sent, and the ways it fails, each
// in plain words with Try again only where trying again can help. Nothing here ever carries report
// content or an exception message; a failure is a code the words are chosen from.
public sealed class FeedbackSendViewModel : Observable
{
    readonly IFeedbackSender sender;
    readonly IUiDispatcher ui;
    CancellationTokenSource? running;
    FeedbackSendState state = FeedbackSendState.Ready;
    string message = "";
    Tone tone = Tone.None;
    bool canRetry;
    int attempts;

    public FeedbackSendViewModel(IFeedbackSender sender, IUiDispatcher? ui = null)
    {
        this.sender = sender;
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
    // Product's words (#738): no beta queue, so offline and every upload failure read the same.
    public const string FailedText = "Couldn't send. Try again when you're online.";
    public string RetryLabel => "Try again";
    // The failure line is the one thing the "Report this" link sits beside.
    public bool Failed => state is FeedbackSendState.Failed or FeedbackSendState.TooLarge or FeedbackSendState.Refused;
    public FeedbackFailureCode? FailureCode { get; private set; }

    // What a screen reader says when the line changes.
    public string Speech => Message;

    // What the person typed and the reviewed preview, held in memory only so Try again sends the same
    // report; there is no beta queue (#738), so nothing is written to disk and Close clears it.
    public FeedbackDraft Draft { get; } = new();

    public void Close()
    {
        Cancel();
        Draft.Clear();
    }

    public Task SendAsync() => Run();

    public Task RetryAsync() => CanRetry ? Run() : Task.CompletedTask;

    public void Cancel() => running?.Cancel();

    async Task Run()
    {
        if (state == FeedbackSendState.Sending || state == FeedbackSendState.Sent) { return; }
        var cancel = new CancellationTokenSource();
        running = cancel;
        attempts++;
        Apply(FeedbackSendState.Sending, "Sending…", Tone.Progress, false, null);
        FeedbackSendResult result;
        try { result = await sender.SendAsync(cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            ui.Post(() => Apply(FeedbackSendState.Ready, "", Tone.None, false, null));
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
                Apply(FeedbackSendState.Sent, $"Thanks. Sent at {s.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}.", Tone.Ok, false, null);
                break;
            case FeedbackFailed f:
                Apply(FeedbackSendState.Failed, FailedText, Tone.Bad, true, f.Code);
                break;
            case FeedbackTooLarge t:
                Apply(FeedbackSendState.TooLarge, $"Not sent: the report is {Words.Size(t.Bytes)}, over the {Words.Size(t.LimitBytes)} limit. Turn off the screenshot or the session turns and send again.", Tone.Bad, false, null);
                break;
            case FeedbackRefused:
                Apply(FeedbackSendState.Refused, "Not sent: the feedback service declined this report. Nothing was kept.", Tone.Bad, false, null);
                break;
            default:
                Apply(FeedbackSendState.Failed, FailedText, Tone.Bad, true, FeedbackFailureCode.Unexpected);
                break;
        }
    }

    void Apply(FeedbackSendState s, string text, Tone t, bool retry, FeedbackFailureCode? code)
    {
        FailureCode = code;
        Message = text;
        Tone = t;
        CanRetry = retry;
        State = s;
        Raise(nameof(CanSend)); Raise(nameof(IsSending)); Raise(nameof(Failed)); Raise(nameof(Speech)); Raise(nameof(FailureCode)); Raise(nameof(Attempts));
    }
}

// In memory for the life of the open window and never persisted.
public sealed class FeedbackDraft
{
    public string TypedText { get; set; } = "";
    public string ReviewedPreview { get; set; } = "";
    public void Clear() { TypedText = ""; ReviewedPreview = ""; }
}
