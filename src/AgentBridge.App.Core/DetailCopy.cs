namespace AgentBridge.App.Core;

// The Copy button on the ID row of a session's Details (#1245), as the PWA's: it puts exactly the
// id on the clipboard, then says "Copied" (or "Copy failed") for a moment. Its name stays
// "Copy session ID" whatever it shows; what happened is announced separately, politely.
public sealed class DetailCopy : Observable
{
    public const string Name = "Copy session ID";
    public const string Ready = "Copy";
    public const string Done = "Copied";
    public const string Failed = "Copy failed";
    public static readonly TimeSpan ResetAfter = CodeCopy.ResetAfter;

    readonly Func<string, bool> put;
    readonly IUiDispatcher ui;
    readonly TimeProvider time;
    ITimer? reset;
    int copies;
    string label = Ready;

    // put: places text on the clipboard, false if it refused (another program can hold it open).
    public DetailCopy(Func<string, bool> put, IUiDispatcher? ui = null, TimeProvider? time = null)
    {
        this.put = put;
        this.ui = ui ?? InlineDispatcher.Instance;
        this.time = time ?? TimeProvider.System;
    }

    public string Label { get => label; private set => Set(ref label, value); }

    public bool Copy(string id)
    {
        bool ok;
        try { ok = put(id); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { ok = false; }
        Label = ok ? Done : Failed;
        reset?.Dispose();
        // A reset already posted can't be recalled by disposing its timer: each carries its copy's
        // number, and one from an earlier copy leaves this copy's word showing.
        var copy = ++copies;
        reset = time.CreateTimer(_ => ui.Post(() => { if (copy == copies) { Label = Ready; } }), null, ResetAfter, Timeout.InfiniteTimeSpan);
        return ok;
    }
}
