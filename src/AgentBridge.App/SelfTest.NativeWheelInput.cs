namespace AgentBridge.App;

interface INativeWheelInputPort
{
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

sealed class NativeWheelInput(INativeWheelInputPort port)
{
    public string Target { get; private set; } = port.DescribeTarget();

    public void Post()
    {
        var target = port.PrepareTarget();
        if (!port.Post(target)) { throw new InvalidOperationException("The native wheel message could not be posted."); }
    }

    public void RecordReceipt(bool received) { }
}
