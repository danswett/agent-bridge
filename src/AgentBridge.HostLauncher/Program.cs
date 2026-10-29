using System.Diagnostics;

// agent-bridge-hostw: starts agent-bridge-host beside it with no console window and waits for
// it, returning its exit code. The logon task runs this, so nothing flashes on screen at
// sign-in, and the task still sees the host's own exit (its restart-on-failure rule depends on
// it). The host stays a console program for the CLI. docs/app-windows.md.
var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host");
var start = new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = AppContext.BaseDirectory,
};
foreach (var a in args) { start.ArgumentList.Add(a); }
using var host = Process.Start(start);
if (host is null) { return 1; }
await host.WaitForExitAsync();
return host.ExitCode;
