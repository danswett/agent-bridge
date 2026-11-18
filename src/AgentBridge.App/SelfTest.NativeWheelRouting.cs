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
        if (hit.Class is not ("Microsoft.UI.Input.InputSite.WindowClass" or "DesktopChildSiteBridge"
                or "Microsoft.UI.Content.DesktopChildSiteBridge")
            || !NativeInputSetup.TargetReady(window, hit.Handle, hit.Root, window,
                thread, process, hit.Thread, hit.Process))
        {
            throw new InvalidOperationException($"The native wheel pointer input site is not verified (HWND: {hit.Handle:X}; class: {hit.Class}; root: {hit.Root:X}; thread: {hit.Thread}; process: {hit.Process}).");
        }
        return hit;
    }
}
