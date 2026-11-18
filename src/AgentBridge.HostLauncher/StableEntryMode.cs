using System.Diagnostics;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Processes;

namespace AgentBridge.HostLauncher;

public static class StableEntryMode
{
    internal static async Task<int?> RunGuardedAsync(string? image, string[] args, Action<string> log,
        Func<ProcessStartInfo, Task<int>>? run = null)
    {
        try
        {
            return await RunAsync(image ?? throw new InvalidOperationException("Missing launcher image path."),
                args, run).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
            or InvalidDataException or System.Text.Json.JsonException)
        {
            log($"stable-entry failed: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    public static async Task<int?> RunAsync(string image, string[] args, Func<ProcessStartInfo, Task<int>>? run = null)
    {
        image = Path.GetFullPath(image);
        var root = Path.GetDirectoryName(image)!;
        var entry = Path.GetFileName(image);
        if (entry is not ("AgentBridge.exe" or "agent-bridge-hostw.exe")
            || !File.Exists(Path.Combine(root, "Update.exe"))
            || SlotEntry.RootForPayload(root) is not null) { return null; }
        // Only the root copies bootstrap. A payload's ordinary hostw retains its supervisor role.
        using var launch = SlotEntry.Resolve(root, entry);
        var info = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launch.Directory
        };
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        return await (run ?? Start)(info).ConfigureAwait(false);
    }

    static async Task<int> Start(ProcessStartInfo info)
    {
        using var child = ProcessLaunch.Start(info) ?? throw new InvalidOperationException("Stable entry did not start.");
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
    }
}
