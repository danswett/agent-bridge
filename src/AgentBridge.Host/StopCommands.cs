namespace AgentBridge.Host;

public static class StopCommands
{
    public const string Usage = "agent-bridge-host stop <session> [--confirm <token>] [--profile <name>]";

    public static Task<int> RunWithProfileAsync(IReadOnlyList<string> args, string root, Func<bool> refused, TextWriter output, TextWriter error)
    {
        error.WriteLine("usage: " + Usage);
        return Task.FromResult(2);
    }
}
