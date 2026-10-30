using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;

namespace AgentBridge.Host;

// The Home Assistant connector from the command line, at the machine (docs/ha-connector.md):
//
//   agent-bridge-host ha status          whether the connector would run, and why not
//   agent-bridge-host ha token set       the token, from standard input; nothing is echoed
//   agent-bridge-host ha token clear
//   agent-bridge-host ha shadow          what the connector would publish beside what the broker
//                                        holds now; reads only, safe while agent-ha-bridge runs
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
                error.WriteLine("usage: agent-bridge-host ha status | ha token set | ha token clear | ha shadow [--json] [--window <seconds>]   [--profile <name>]");
                return 2;
        }
    }

    // The read-only shadow (HaShadow): what this host would publish beside what the broker holds,
    // while agent-ha-bridge still runs the machine. Needs the address and the token, not
    // homeAssistant.enabled: it is how a machine is checked before the connector is turned on.
    public static async Task<int> ShadowAsync(string folder, ISessionSource source, bool json, string? windowSeconds, string version, bool devBuild,
        Func<HostSettings, string, IHomeAssistant>? connect, TextWriter output, TextWriter error, CancellationToken cancellationToken = default,
        Func<(AgentBridge.Protocol.UsageReport Report, long Version)>? usage = null)
    {
        var (settings, problem) = HostSettings.Load(folder);
        if (problem is not null) { error.WriteLine(problem); }
        var ha = settings.HomeAssistant;
        if (ha.BaseUrl is null) { error.WriteLine("homeAssistant.baseUrl is not set; nothing to compare with"); return 1; }
        string? token;
        try { token = HaTokenStore.Load(folder); }
        catch (System.Security.Cryptography.CryptographicException e) { error.WriteLine($"the Home Assistant token could not be unprotected ({e.Message})"); return 1; }
        if (string.IsNullOrEmpty(token)) { error.WriteLine("no Home Assistant token; give it one with agent-bridge-host ha token set"); return 1; }
        var window = TimeSpan.FromSeconds(3);
        if (windowSeconds is not null)
        {
            if (!double.TryParse(windowSeconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= 0 || s > 60)
            {
                error.WriteLine("--window is a number of seconds, up to 60");
                return 2;
            }
            window = TimeSpan.FromSeconds(s);
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var client = connect?.Invoke(settings, token) ?? new HomeAssistantClient(ha.BaseUrl, token, http);
        var machine = ha.MachineName ?? Environment.MachineName;
        var report = await HaShadow.CompareAsync(client, source, new HaConnectorOptions
        {
            MachineName = machine,
            Version = version,
            Dev = devBuild,
            DetailedActivityFallback = ha.DetailedActivity,
            DashboardUrlPath = ha.DashboardUrlPath,
            AgentUserIds = ha.AgentUserIds,
            UsageAccountSensors = usage is null ? [] : HaContract.UsageAccountSensors(settings.Usage.CopilotAccounts.Select(a => a.Slot)),
        }, servesAnswers: OperatingSystem.IsWindows(), window, usage, cancellationToken);
        if (json)
        {
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
            return 0;
        }
        output.WriteLine($"Shadow comparison for {report.Machine} (agent_bridge_{report.Slug}) against {ha.BaseUrl}. Read-only: nothing was written.");
        output.WriteLine($"  sessions: this host {report.HostNodes.Count}, the broker's list {report.DaemonNodes.Count}");
        foreach (var n in report.HostNodes.Except(report.DaemonNodes)) { output.WriteLine($"    only this host sees {n}"); }
        foreach (var n in report.DaemonNodes.Except(report.HostNodes)) { output.WriteLine($"    only agent-ha-bridge lists {n}"); }
        var byVerdict = report.Differences.GroupBy(d => d.Verdict).ToDictionary(g => g.Key, g => g.Count());
        output.WriteLine($"  topics: {report.Same} the same, {byVerdict.GetValueOrDefault(HaShadow.Differs)} different, " +
            $"{byVerdict.GetValueOrDefault(HaShadow.DaemonOnly)} agent-ha-bridge only ({report.Differences.Count(d => d.Note is not null)} by design), {byVerdict.GetValueOrDefault(HaShadow.HostOnly)} this host only");
        foreach (var d in report.Differences.Where(d => d.Note is null || d.Verdict == HaShadow.Differs))
        {
            output.WriteLine($"  {d.Verdict,-22} {d.Topic}{(d.Note is null ? "" : "  (" + d.Note + ")")}");
        }
        return 0;
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
