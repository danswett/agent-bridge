using AgentBridge.HomeAssistant;

namespace AgentBridge.Host;

// The Home Assistant connector from the command line, at the machine (docs/ha-connector.md):
//
//   agent-bridge-host ha status          whether the connector would run, and why not
//   agent-bridge-host ha token set       the token, from standard input; nothing is echoed
//   agent-bridge-host ha token clear
//
// The token is never taken as an argument, where it would sit in the shell's history and the
// process list, and never printed.
public static class HaCommands
{
    // Whether these arguments change the stored token, which decides whether and where the
    // connector runs: for a person only (Program's Refused).
    public static bool Writes(IReadOnlyList<string> args) =>
        args.ElementAtOrDefault(1) == "token" && args.ElementAtOrDefault(2) is "set" or "clear";

    public static int Run(IReadOnlyList<string> args, string folder, Func<string?> readSecret, TextWriter output, TextWriter error,
        bool devBuild, Func<string?>? agentHaBridge)
    {
        switch (args.ElementAtOrDefault(1), args.ElementAtOrDefault(2))
        {
            case ("status", _):
                return Status(folder, output, devBuild, agentHaBridge);
            case ("token", "set"):
                {
                    var token = readSecret();
                    if (string.IsNullOrWhiteSpace(token)) { error.WriteLine("No token was given; nothing changed."); return 1; }
                    HaTokenStore.Save(folder, token);
                    output.WriteLine($"Stored the Home Assistant token, protected{(OperatingSystem.IsWindows() ? " (DPAPI, this Windows user)" : " (readable by you only)")}, in {folder}. Restart agent-bridge-host serve to use it.");
                    return 0;
                }
            case ("token", "clear"):
                HaTokenStore.Clear(folder);
                output.WriteLine("Removed the Home Assistant token. Restart agent-bridge-host serve to stop using it.");
                return 0;
            default:
                error.WriteLine("usage: agent-bridge-host ha status | ha token set | ha token clear   [--profile <name>]");
                return 2;
        }
    }

    static int Status(string folder, TextWriter output, bool devBuild, Func<string?>? agentHaBridge)
    {
        var (settings, problem) = HostSettings.Load(folder);
        if (problem is not null) { output.WriteLine(problem); }
        foreach (var p in settings.HomeAssistantProblems) { output.WriteLine(p); }
        var ha = settings.HomeAssistant;
        var machine = ha.MachineName ?? Environment.MachineName;
        var slug = HaContract.MachineSlug(machine);
        var why = HaGate.WhyNot(ha, devBuild, HaTokenStore.Exists(folder), agentHaBridge is not null);
        output.WriteLine($"Home Assistant connector: {why ?? "on"}");
        output.WriteLine($"  Home Assistant: {ha.BaseUrl?.ToString() ?? "(not set)"}{(ha.Disposable ? " (disposable)" : "")}");
        output.WriteLine($"  token: {(HaTokenStore.Exists(folder) ? "stored, protected" : "none")}");
        output.WriteLine($"  this machine: {machine}, entities agent_bridge_{slug}_*, topics {HaContract.MachineTopicRoot(slug)}/");
        output.WriteLine($"  dashboard read for retirement: {ha.DashboardUrlPath}");
        if (why is null && agentHaBridge?.Invoke() is { } active) { output.WriteLine($"  paused now: {active}"); }
        return 0;
    }

    // Reads a line without echoing it when a person is typing; a pipe is read as it is.
    public static string? ReadSecret(TextWriter prompt)
    {
        if (Console.IsInputRedirected) { return Console.In.ReadLine(); }
        prompt.Write("Home Assistant long-lived access token (not shown): ");
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { break; }
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) { text.Length--; } continue; }
            if (!char.IsControl(key.KeyChar)) { text.Append(key.KeyChar); }
        }
        prompt.WriteLine();
        return text.ToString();
    }
}
