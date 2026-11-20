using System.Diagnostics;
using AgentBridge.App.Core;

namespace AgentBridge.HostLauncher;

public static class UpdateCoordinatorStartInfo
{
    internal const string AppExe = "AgentBridge.exe";

    public static ProcessStartInfo ApplyStart(UpdateCoordinatorRequest request)
    {
        var info = new ProcessStartInfo(Path.Combine(request.Root, "Update.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = request.Root
        };
        foreach (var arg in new[] { "--silent", "apply", "--norestart", "--package", request.Package,
            "--rootDir", request.Root, "--packageDir", request.Packages }) info.ArgumentList.Add(arg);
        return info;
    }

    public static ProcessStartInfo AppStart(UpdateCoordinatorRequest request)
    {
        var current = Path.Combine(request.Root, "current");
        var info = new ProcessStartInfo(Path.Combine(current, AppExe))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = current
        };
        foreach (var arg in request.RestartArguments) info.ArgumentList.Add(arg);
        return info;
    }
}
