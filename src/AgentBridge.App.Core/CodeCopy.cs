namespace AgentBridge.App.Core;

// A code block's Copy button (#296), as the PWA's (pwa/js/markdown.js codeBlock): what it puts on
// the clipboard and what it says afterwards. What goes is the block as the agent wrote it, every
// space, tab and line break, never the lines it is wrapped into on screen: a long command pasted
// into a shell must be the one command it was, not one broken wherever the window ended.
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
    string label = Ready;

    // put: places text on the clipboard, false if the clipboard refused it (another program can
    // hold it open). Its timer's callback comes back through ui, on the UI thread in the app.
    public CodeCopy(MdCode code, Func<string, bool> put, IUiDispatcher? ui = null, TimeProvider? time = null)
    {
        Text = code.Text;
        this.put = put;
        this.ui = ui ?? InlineDispatcher.Instance;
        this.time = time ?? TimeProvider.System;
    }

    public string Text { get; }

    // What the block draws: its text without the blank lines a fence may end with, as the app
    // always drew it. Copy still takes them, as the PWA's does.
    public string Shown => Text.TrimEnd('\n');

    public string Label { get => label; private set => Set(ref label, value); }

    public bool Copy()
    {
        bool ok;
        try { ok = put(Text); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { ok = false; }
        Label = ok ? Done : Failed;
        reset?.Dispose();
        reset = time.CreateTimer(_ => ui.Post(Reset), null, ResetAfter, Timeout.InfiniteTimeSpan);
        return ok;
    }

    void Reset() => Label = Ready;
}
