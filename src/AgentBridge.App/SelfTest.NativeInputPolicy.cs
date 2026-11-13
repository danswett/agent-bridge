namespace AgentBridge.App;

readonly record struct NativeInputTarget(nint Window, nint Focus, nint Root, nint Foreground,
    uint Thread, uint Process, uint WindowThread, uint WindowProcess)
{
    public bool Ready => Window != 0 && Focus != 0 && Focus != Window && Root == Window
        && Foreground == Window && WindowThread != 0 && Thread == WindowThread
        && WindowProcess != 0 && Process == WindowProcess;
}
