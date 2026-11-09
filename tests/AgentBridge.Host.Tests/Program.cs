using AgentBridge.Host;

namespace AgentBridge.Host.Tests;

// The test SDK's generated entry point does nothing. This one does the same unless a test starts
// the assembly as a child process with "agent-sign-child <profile folder> <start tick>": it waits
// for that instant so every child races together, makes or opens the key and prints its public point.
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "agent-sign-child") { return 0; }
        var wait = new DateTime(long.Parse(args[2]), DateTimeKind.Utc) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) { Thread.Sleep(wait); }
        using var key = AgentSignKey.OpenOrCreate(args[1], true);
        Console.Out.WriteLine(Convert.ToHexString(key.PublicPoint));
        return 0;
    }
}
