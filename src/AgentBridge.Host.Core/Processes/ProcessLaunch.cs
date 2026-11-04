using System.Diagnostics;

namespace AgentBridge.Host.Core.Processes;

// Every Windows host subprocess start shares this gate. A native launch's HANDLE_LIST restricts
// its own child, but cannot stop another concurrent Process.Start inheriting its pipes.
public static class ProcessLaunch
{
    static readonly object Gate = new();

    public static Process? Start(ProcessStartInfo start)
    {
        lock (Gate) { return Process.Start(start); }
    }

    // Release on the creating thread, before awaiting process completion.
    public static IDisposable HoldInheritableHandles()
    {
        Monitor.Enter(Gate);
        return new Lease();
    }

    sealed class Lease : IDisposable
    {
        object? held = Gate;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref held, null) is { } gate) { Monitor.Exit(gate); }
        }
    }
}
