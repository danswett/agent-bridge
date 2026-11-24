using System.Diagnostics;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

// The reported failure end to end, with real processes and the real job: a shell whose parent exited (so its parent-pid chain stops short of
// the session root) is accepted once the launch is bound, and a process that is in neither the chain nor the job is still refused.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AgentCredentialRealJobTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abrealjob-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<Process> started = [];
    readonly List<int> stray = [];

    public AgentCredentialRealJobTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        foreach (var p in started) { try { if (!p.HasExited) { p.Kill(true); } } catch (Exception) { } p.Dispose(); }
        foreach (var id in stray) { try { using var q = Process.GetProcessById(id); q.Kill(); } catch (Exception) { } }
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    static DateTimeOffset StartOf(Process p) => new(p.StartTime.ToUniversalTime());

    Process Sleeper()
    {
        var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 120") { CreateNoWindow = true, UseShellExecute = false })!;
        started.Add(p);
        return p;
    }

    [Fact]
    public void A_shell_whose_parent_exited_is_accepted_through_the_job_and_a_stranger_is_still_refused()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = new AgentCredentials(folder, "machine1", rootAlive: AgentCredentials.SameProcessAlive, jobs: new WindowsSessionJobs());

        // R (the session root) sleeps, then starts M; M starts C, writes C's pid and exits: C is a grandchild with no parent left.
        var pidFile = Path.Combine(folder, "child.pid");
        File.WriteAllText(Path.Combine(folder, "m.ps1"),
            $"$c = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120'\r\n$c.Id | Set-Content -LiteralPath '{pidFile}'\r\n");
        File.WriteAllText(Path.Combine(folder, "r.ps1"),
            $"Start-Sleep -Seconds 4\r\nStart-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile','-File','{Path.Combine(folder, "m.ps1")}'\r\nStart-Sleep -Seconds 120\r\n");
        var token = store.Prepare("sess-1", "launch-1")!;
        var root = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -File \"{Path.Combine(folder, "r.ps1")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        started.Add(root);
        store.Bind("launch-1", root.Id, StartOf(root));

        for (var i = 0; i < 300 && !File.Exists(pidFile); i++) { Thread.Sleep(100); }
        Assert.True(File.Exists(pidFile), "the grandchild never started");
        var child = int.Parse(File.ReadAllText(pidFile).Trim());
        // M writes the pid and then exits; the grandchild is an orphan only once M is gone (a busy machine can be slow to get there).
        for (var i = 0; i < 150 && ProcessChain.Read(child).Processes.Any(p => p.Id == root.Id); i++) { Thread.Sleep(200); }
        stray.Add(child);

        var orphan = ProcessChain.Read(child);
        Assert.DoesNotContain(orphan.Processes, p => p.Id == root.Id);
        Assert.NotNull(store.Check(token, orphan).Accepted);

        var stranger = Sleeper();
        Assert.Null(store.Check(token, ProcessChain.Read(stranger.Id)).Accepted);
        // The root itself, and a shell still attached to it, pass by the ancestor rule as before.
        Assert.NotNull(store.Check(token, ProcessChain.Read(root.Id)).Accepted);
    }
}
