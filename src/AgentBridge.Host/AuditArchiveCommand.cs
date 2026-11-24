using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public static class AuditArchiveCommand
{
    public static int Run(IReadOnlyList<string> args, string profileRoot, TextWriter output, TextWriter error)
    {
        var profile = "default";
        if (args.Count != 2 && (args.Count != 4 || args[2] != "--profile"
            || !Identity.IsValidProfile(args[3])))
        {
            error.WriteLine("usage: agent-bridge-host audit archive [--profile <name>]");
            return 2;
        }
        if (args.Count == 4) { profile = args[3]; }
        try
        {
            var count = new AgentAuditLog(Path.Combine(profileRoot, profile), log: error.WriteLine).Archive(DateTimeOffset.UtcNow);
            output.WriteLine($"Archived {count} audit files.");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            error.WriteLine("audit archive: refused; audit evidence could not be verified or archiving could not be completed.");
            return 1;
        }
    }
}
