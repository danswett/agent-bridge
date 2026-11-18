namespace AgentBridge.App;

interface INativeWheelInputPort
{
    bool ListIsTabStop { get; set; }
    bool FocusList();
    bool ListHasFocus { get; }
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

    public void Post()
    {
        // The conversation is normally not a Tab stop, so WinUI otherwise refuses Focus().
        port.ListIsTabStop = true;
        if (!port.FocusList()) { throw new InvalidOperationException($"setup: native wheel list focus failed ({port.DescribeTarget()})"); }
        var target = port.PrepareTarget();
        if (!port.ListHasFocus || !port.TargetIsVerified(target) || !port.WindowIsForeground)
        {
            throw new InvalidOperationException($"setup: native wheel target not ready ({port.DescribeTarget()})");
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
