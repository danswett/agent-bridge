namespace AgentBridge.App;

static class NativeInputSetup
{
    public static void RequireForeground(Func<bool> acquireAndVerify, Func<string> describeForeground) =>
        RequireForeground(acquireAndVerify, describeForeground, () => { });

    public static void RequireForeground(Func<bool> acquireAndVerify, Func<string> describeForeground, Action beforeRetry)
    {
        if (acquireAndVerify()) { return; }
        beforeRetry();
        if (!acquireAndVerify()) { throw new InvalidOperationException($"setup: window not foreground ({describeForeground()})"); }
    }

    public static bool AcquireForeground(Func<bool> ownsForeground, Func<bool> attach, Action activate, Action<bool> release)
    {
        if (ownsForeground()) { return true; }
        var attached = false;
        try { attached = attach(); activate(); }
        finally { release(attached); }
        return ownsForeground();
    }

    public static bool CanMinimizeTerminal(bool hostedArm64, string? windowClass, string? process) =>
        hostedArm64 && windowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
        && string.Equals(process, "WindowsTerminal", StringComparison.OrdinalIgnoreCase);

    public static string Foreground(string? className, string? title, string? process) =>
        $"class: {Quote(className)}; title: {Quote(title)}; process: {Quote(process)}";

    public static bool TargetReady(nint window, nint focus, nint root, nint foreground,
        uint thread, uint process, uint windowThread, uint windowProcess) =>
        window != 0 && focus != 0 && focus != window && root == window
        && foreground == window && windowThread != 0 && thread == windowThread
        && windowProcess != 0 && process == windowProcess;

    static string Quote(string? value) => System.Text.Json.JsonSerializer.Serialize(string.IsNullOrEmpty(value) ? "unknown" : value);
}
