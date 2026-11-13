namespace AgentBridge.App;

static class NativeInputSetup
{
    public static void RequireForeground(Func<bool> acquireAndVerify, Func<string> describeForeground)
    {
        if (!acquireAndVerify() && !acquireAndVerify()) { throw new InvalidOperationException($"setup: window not foreground ({describeForeground()})"); }
    }

    public static string Foreground(string? className, string? title, string? process) =>
        $"class: {Quote(className)}; title: {Quote(title)}; process: {Quote(process)}";

    public static bool TargetReady(nint window, nint focus, nint root, nint foreground,
        uint thread, uint process, uint windowThread, uint windowProcess) =>
        window != 0 && focus != 0 && focus != window && root == window
        && foreground == window && windowThread != 0 && thread == windowThread
        && windowProcess != 0 && process == windowProcess;

    static string Quote(string? value) => System.Text.Json.JsonSerializer.Serialize(string.IsNullOrEmpty(value) ? "unknown" : value);
}
