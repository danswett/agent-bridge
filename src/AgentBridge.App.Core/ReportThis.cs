namespace AgentBridge.App.Core;

// What a "Report this" press hands to the feedback flow: which kind of action failed and the
// delivery's own state key. Never the line's text or anything the person typed, so opening the
// report cannot put message content into the bundle by itself.
public sealed record ReportThisContext(string Action, string FailureKey);

// The inline "Report this" beside a failed action's error (#738 S4). Offered only for a line that
// is an error and has a cause the person cannot clear themselves.
public sealed class ReportThisOffer
{
    public static readonly string Label = "Report this";

    readonly ReportThisContext? context;

    ReportThisOffer(ReportThisContext? context, string speech) { this.context = context; Speech = speech; }

    public static readonly ReportThisOffer None = new(null, "");

    public bool Visible => context is not null;
    public ReportThisContext? Context => context;
    // Names the failure so a screen reader does not hear a bare "Report this" link.
    public string Speech { get; }

    public static ReportThisOffer For(string action, ReceiptLine line) =>
        line.Tone == Tone.Bad && line.Key.Length > 0 && line.Key is not ("none" or "quiet")
            ? new(new ReportThisContext(action, line.Key), $"{Label}: {Words.Sentence(line.Text)}")
            : None;

    public bool Press(Action<ReportThisContext> open)
    {
        if (context is null) { return false; }
        open(context);
        return true;
    }
}
