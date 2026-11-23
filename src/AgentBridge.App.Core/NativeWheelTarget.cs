namespace AgentBridge.App.Core;

// #3982: which window the self-test posts its native WM_MOUSEWHEEL to. A real wheel is hit-tested to the window under the pointer, not
// to the focus window the posted F24 key goes to; on the hosted ARM64 installer the wheel posted to the focus window never arrives
// while the key does. So the wheel is aimed at the window at the list's screen point when that is one of the app's own (its top-level
// window is the app's), and falls back to the focus window as before when it is not (nothing there, or another process's).
public static class NativeWheelTarget
{
    public static nint Choose(nint appWindow, nint atPoint, nint focus, Func<nint, nint> topLevelOf) =>
        atPoint != 0 && appWindow != 0 && topLevelOf(atPoint) == appWindow ? atPoint : focus;
}
