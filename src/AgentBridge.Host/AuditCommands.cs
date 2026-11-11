namespace AgentBridge.Host;

public static class AuditCommands
{
    public static int Run(IReadOnlyList<string> args, string profileRoot, TextWriter output, TextWriter error)
    {
        error.WriteLine("The audit read command is not implemented.");
        return 2;
    }
}
