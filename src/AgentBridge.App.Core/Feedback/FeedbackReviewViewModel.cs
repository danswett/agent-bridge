namespace AgentBridge.App.Core.Feedback;

// One line of "What's included" on the review screen.
public sealed record FeedbackReviewRow(string Label, string Summary, bool Optional);

// The review half of the Send feedback dialog (#738 S3a-1): what will be sent, and the choices that
// change it. Every change to the note, screenshot or session-turns option gathers again for a new
// FeedbackRequest and hands the result to the send view model; a gather that finishes after a newer
// request, or after Close, is dropped. Nothing is sent from here; the person presses Send, which
// calls FeedbackSendViewModel.SendAsync.
public sealed class FeedbackReviewViewModel : Observable
{
    readonly IFeedbackBundleSource source;
    readonly FeedbackSendViewModel send;
    readonly IUiDispatcher ui;
    readonly object gate = new();
    CancellationTokenSource? running;
    int generation;
    bool closed;
    string note = "";
    byte[]? screenshot;
    bool includeTurns;
    bool rowsExpanded;
    IReadOnlyList<FeedbackReviewRow> rows = Array.Empty<FeedbackReviewRow>();

    // sessionId and sessionTitle name the session whose last turns the option would attach; with no
    // session the option is unavailable.
    public FeedbackReviewViewModel(IFeedbackBundleSource source, FeedbackSendViewModel send, string? sessionId = null, string? sessionTitle = null, IUiDispatcher? ui = null)
    {
        this.source = source;
        this.send = send;
        this.ui = ui ?? InlineDispatcher.Instance;
        SessionId = sessionId;
        SessionTitle = sessionTitle;
    }

    public string? SessionId { get; }
    public string? SessionTitle { get; }
    public bool HasSession => !string.IsNullOrEmpty(SessionId);
    public string TurnsLabel => $"Include the last turns of \"{SessionTitle ?? SessionId}\"";

    public IReadOnlyList<FeedbackReviewRow> Rows { get => rows; private set => Set(ref rows, value); }

    // The rows are collapsed until the person opens "What's included".
    public bool RowsExpanded { get => rowsExpanded; set => Set(ref rowsExpanded, value); }

    public string Note
    {
        get => note;
        set { if (note != value) { note = value; Raise(nameof(Note)); Regather(); } }
    }

    public byte[]? ScreenshotPng
    {
        get => screenshot;
        set { if (!ReferenceEquals(screenshot, value)) { screenshot = value; Raise(nameof(ScreenshotPng)); Regather(); } }
    }

    // Off by default; it names one session and only applies when there is one.
    public bool IncludeSessionTurns
    {
        get => includeTurns;
        set
        {
            if (value && !HasSession) { value = false; }
            if (includeTurns != value) { includeTurns = value; Raise(nameof(IncludeSessionTurns)); Regather(); }
        }
    }

    // First gather, when the dialog opens.
    public void Start() => Regather();

    public void Close()
    {
        CancellationTokenSource? cancel;
        lock (gate)
        {
            if (closed) { return; }
            closed = true;
            generation++;
            cancel = running;
            running = null;
        }
        try { cancel?.Cancel(); } catch (ObjectDisposedException) { }
        send.Close();
    }

    void Regather()
    {
        FeedbackRequest request;
        CancellationTokenSource cancel;
        CancellationTokenSource? previous;
        int mine;
        // What is being sent, or was sent, is fixed; edits after that don't change the shown rows.
        if (send.State is FeedbackSendState.Sending or FeedbackSendState.Sent) { return; }
        lock (gate)
        {
            if (closed) { return; }
            mine = ++generation;
            previous = running;
            cancel = running = new CancellationTokenSource();
            request = new FeedbackRequest { Note = note, ScreenshotPng = screenshot, SessionTurnsFor = includeTurns ? SessionId : null };
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        send.BeginGathering();
        _ = GatherAsync(request, mine, cancel);
    }

    async Task GatherAsync(FeedbackRequest request, int mine, CancellationTokenSource cancel)
    {
        FeedbackGatherResult? result = null;
        try { result = await source.GatherAsync(request, null, cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        catch { }
        ui.Post(() =>
        {
            lock (gate) { if (closed || generation != mine) { return; } }
            switch (result)
            {
                case FeedbackGatherResult.Ready ready:
                    Rows = ready.Bundle.Items.Select(i => new FeedbackReviewRow(i.Label, i.Summary, i.Optional)).ToList();
                    send.Ready(ready.Bundle);
                    break;
                case FeedbackGatherResult.Refused refused:
                    Rows = Array.Empty<FeedbackReviewRow>();
                    send.GatherRefused(refused.Reason);
                    break;
                default:
                    Rows = Array.Empty<FeedbackReviewRow>();
                    send.GatherFailed();
                    break;
            }
        });
    }
}
