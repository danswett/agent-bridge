namespace AgentBridge.App;

interface INativeWheelInputPort
{
    bool ListIsTabStop { get; set; }
    Task<bool> FocusList();
    bool ListHasFocus { get; }
    bool ListAtEnd { get; }
    nint PrepareTarget();
    bool TargetIsVerified(nint target);
    bool WindowIsForeground { get; }
    bool Post(nint target);
    string DescribeTarget();
    IReadOnlyList<nint> ReadHwndChain();
    void Log(string message);
}

sealed class NativeWheelInput(INativeWheelInputPort port) : IDisposable
{
    readonly bool wasTabStop = port.ListIsTabStop;
    public string Target { get; private set; } = port.DescribeTarget();

    public void Dispose() => port.ListIsTabStop = wasTabStop;

    public async Task Post()
    {
        port.ListIsTabStop = true;
        // A focus request can fail even though a child already holds the required focus.
        var focusRequest = await port.FocusList();
        if (!port.ListAtEnd) { throw new InvalidOperationException("setup: native wheel focus moved the view away from the latest."); }
        var target = port.PrepareTarget();
        var listFocus = port.ListHasFocus;
        var nativeTarget = port.TargetIsVerified(target);
        var foreground = port.WindowIsForeground;
        if (!listFocus || !nativeTarget || !foreground)
        {
            var failed = string.Join(", ", new[]
            {
                listFocus ? null : "list focus", nativeTarget ? null : "native target", foreground ? null : "foreground",
            }.Where(condition => condition is not null));
            throw new InvalidOperationException($"setup: native wheel target not ready (failed: {failed}; "
                + $"focus request: {focusRequest}; list focus: {listFocus}; native target: {nativeTarget}; foreground: {foreground}; "
                + $"prepared target: {target:X}; {port.DescribeTarget()})");
        }
        Target = port.DescribeTarget();
        if (!port.Post(target)) { throw new InvalidOperationException("The native wheel message could not be posted."); }
    }

    public void RecordReceipt(bool received)
    {
        if (received) { return; }
        var chain = port.ReadHwndChain();
        Target = $"self-test: wheel not received; before: {Target}; now: {port.DescribeTarget()}; HWND chain: "
            + (chain.Count == 0 ? "(empty)" : string.Join(" -> ", chain.Select(hwnd => $"0x{hwnd:X}")));
        port.Log(Target);
    }
}
