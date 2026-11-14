using AgentBridge.Host;

namespace AgentBridge.Host.Tests;

// The test SDK's generated entry point does nothing. This one does the same unless a test starts
// the assembly as a child process with "agent-sign-child <profile folder> <start tick>": it waits
// for that instant so every child races together, makes or opens the key and prints its public point.
public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["enrollment-command-child"])
        {
            var result = typeof(HostSettings).Assembly.EntryPoint!.Invoke(null, [new[] { "devices", "descriptor", "--profile", "private-sentinel:invalid" }]);
            return result is Task<int> pending ? pending.GetAwaiter().GetResult() : (int)result!;
        }
        if (args is ["hook-readiness-contention"] && OperatingSystem.IsWindows())
        {
            return HookReadinessWithBusyThreadPool();
        }
        if (args.Length == 3 && args[0] == "host-sign-open" && OperatingSystem.IsWindows())
        {
            using var hostSign = HostSignKey.LoadOrCreate(args[1]);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hostSign.PublicPoint));
            return digest == args[2] ? 0 : 1;
        }
        if (args.Length != 3 || args[0] != "agent-sign-child") { return 0; }
        var wait = new DateTime(long.Parse(args[2]), DateTimeKind.Utc) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) { Thread.Sleep(wait); }
        using var key = AgentSignKey.OpenOrCreate(args[1], true);
        Console.Out.WriteLine(Convert.ToHexString(key.PublicPoint));
        return 0;
    }

    static int HookReadinessWithBusyThreadPool()
    {
        // Only this child is constrained; the parallel test host keeps its own thread pool.
        if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, 1))
        {
            Console.Error.WriteLine("Could not constrain the readiness probe's thread pool.");
            return 1;
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            entered.Set();
            try { release.Wait(); }
            finally { finished.Set(); }
        });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("The contention worker did not start.");
                return 1;
            }
            using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                "/d /c set /p answer=ready")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
            })!;
            try
            {
                var ready = global::StandaloneHooksTests.ReadHookReadinessAsync(running);
                // A synchronous wait keeps the deadline independent of the occupied pool.
                if (!ready.Wait(TimeSpan.FromSeconds(5)))
                {
                    Console.Error.WriteLine("The hook stdout readiness read waited for the occupied thread pool.");
                    return 1;
                }
                if (ready.Result > 0 && !running.HasExited) { return 0; }
                Console.Error.WriteLine("The hook exited or did not emit its readiness prompt.");
                return 1;
            }
            finally
            {
                release.Set();
                if (!running.HasExited) { running.Kill(entireProcessTree: true); }
                running.WaitForExit();
            }
        }
        finally
        {
            release.Set();
            if (!finished.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The readiness probe's contention worker did not stop.");
            }
        }
    }
}
