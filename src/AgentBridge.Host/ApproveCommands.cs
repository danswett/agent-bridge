namespace AgentBridge.Host;

public static class ApproveCommands
{
    public static Task<int> RunAsync(string[] args, string root, Func<bool> refused,
        TextReader input, TextWriter output, TextWriter error)
    {
        error.WriteLine("usage: approve <session> <question> --request <uuid> [--profile <name>]");
        return Task.FromResult(2);
    }
}
