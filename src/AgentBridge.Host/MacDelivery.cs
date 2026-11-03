using System.ComponentModel;
using AgentBridge.Host.Delivery.Mac;

namespace AgentBridge.Host;

internal static class MacDelivery
{
    public static ManagedTmuxBackend? Create(string profileFolder, Action<string> log)
    {
        if (!OperatingSystem.IsMacOS()) { return null; }
        var executable = FindTmux(Environment.GetEnvironmentVariable("PATH"));
        if (executable is null)
        {
            log("Mac session control is off: install tmux and put its absolute directory on PATH, then restart the host.");
            return null;
        }
        try
        {
            var backend = new ManagedTmuxBackend(executable, Path.Combine(profileFolder, "run"));
            if (backend.Unavailable() is { } why) { log($"Mac session control is off: {why}"); return null; }
            log($"Mac sessions use a private tmux server: {backend.SocketPath}. Only sessions launched by this host run can be controlled.");
            return backend;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            log($"Mac session control is off: {e.Message}");
            return null;
        }
    }

    internal static string? FindTmux(string? searchPath)
    {
        foreach (var directory in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Relative PATH entries would let a workspace choose the control binary.
            if (!Path.IsPathFullyQualified(directory)) { continue; }
            var path = Path.Combine(directory, "tmux");
            if (!File.Exists(path)) { continue; }
            if (OperatingSystem.IsWindows()) { return path; }
            try
            {
                if ((File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) { return path; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
