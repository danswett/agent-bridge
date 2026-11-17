namespace AgentBridge.App;

readonly record struct NativeWheelTarget(nint Handle, string Class, nint Root, uint Thread, uint Process);

static class NativeWheelRouting
{
    public static NativeWheelTarget Select(nint window, uint thread, uint process,
        NativeWheelTarget focus, NativeWheelTarget hit)
    {
        if (!NativeInputSetup.TargetReady(window, focus.Handle, focus.Root, window,
            thread, process, focus.Thread, focus.Process))
        {
            throw new InvalidOperationException("The native wheel focus is not owned by the app.");
        }
        return focus;
    }
}
