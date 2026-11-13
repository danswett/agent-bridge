namespace AgentBridge.Host;

internal static class HostSignCommands
{
    public static int Run(IReadOnlyList<string> args, string root, Func<Caller> caller, bool credential, TextWriter output, TextWriter error)
    {
        error.WriteLine("{\"state\":\"unavailable\"}");
        return 2;
    }
}
