using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal static class HostSignCommands
{
    public static int Run(IReadOnlyList<string> args, string root, Func<Caller> caller, bool credential, TextWriter output, TextWriter error)
    {
        if (credential || caller().Kind != CallerKind.HostCli) { error.WriteLine("{\"state\":\"refused\"}"); return 4; }
        var profile = args switch
        {
            ["host-sign", "init"] => "default",
            ["host-sign", "init", "--profile", var name] => name,
            _ => null,
        };
        if (profile is null) { error.WriteLine("{\"state\":\"invalid-command\"}"); return 2; }
        if (!Identity.IsValidProfile(profile)) { error.WriteLine("{\"state\":\"invalid-profile\"}"); return 2; }
        if (!OperatingSystem.IsWindows()) { error.WriteLine("{\"state\":\"unsupported\"}"); return 2; }
        var folder = Path.Combine(root, profile);
        var existed = HostSignCustody.Read(folder).Result == "available";
        HostSigningStatus status;
        try
        {
            using (HostSignKey.LoadOrCreate(folder)) { }
            status = HostSignCustody.Read(folder);
        }
        catch (InvalidDataException e) when (e.Message == "missing-key")
        {
            status = new("missing-key");
        }
        var available = status.Result == "available";
        output.WriteLine(JsonSerializer.Serialize(new
        {
            state = available ? existed ? "existing" : "created" : "missing-key",
            keyId = status.KeyId,
        }, ProtocolJson.Options));
        return available ? 0 : 1;
    }
}
