using AgentBridge.Host.Core.DevBox;
using AgentBridge.Local;

namespace AgentBridge.Host;

// The Dev Box keep-awake from the command line (docs/host-config.md#dev-box-keep-awake):
//
//   agent-bridge-host devbox status    whether this is a Dev Box, the profile's devBox settings, and
//                                      whether agent-ha-bridge's own keep-awake is on here (so this
//                                      host stands back). Reads only this machine: no Dev Center call
//   agent-bridge-host devbox dry-run   one pass that reads this Dev Box's pending stops from Dev
//                                      Center and says what it would do, changing nothing, as
//                                      agent-ha-bridge's agent-bridge-devbox-keepawake.ps1 -DryRun
//
// There is deliberately no pass by hand that changes the schedule: serve runs them, one at a time,
// and a second writer is exactly what the keep-awake is built to avoid.
public static class DevBoxCommands
{
    // HostStatus.DevBox: present whenever the Dev Box agent's settings file is here, usable or not,
    // so the app shows the setting and, when the file cannot be used, the alarm, rather than hiding
    // the section as if this were not a Dev Box while it hibernates.
    public static DevBoxStatus? Status(KeepAwakeState? state) => state is { IsDevBox: true } k
        ? new DevBoxStatus(k.Enabled, k.IntervalMinutes, k.Last is { } last ? new DevBoxPass(k.LastAt!.Value, last.Outcome, last.Detail, last.Alarming) : null, k.NextAt, k.Problem)
        : null;
    public static async Task<int> RunAsync(IReadOnlyList<string> args, HostSettings settings, string? settingsProblem, string log,
        Func<(DevBoxIdentity? Identity, string? Problem)> findIdentity, Func<KeepAwakeOwner?> agentHaBridgeKeepAwake, Func<KeepAwakeService> service,
        TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        switch (args.ElementAtOrDefault(1))
        {
            case "status":
                {
                    var (identity, problem) = findIdentity();
                    output.WriteLine(identity is { } i
                        ? $"This machine is a Dev Box: {i.DevBox}, in project {i.Project} of {i.Endpoint.Host}."
                        : problem is { } p
                            ? $"This machine is a Dev Box, but its agent settings could not be used, so it cannot be kept awake: {p}."
                            : "This machine is not a Dev Box.");
                    if (settingsProblem is not null) { output.WriteLine(settingsProblem); }
                    foreach (var wrong in settings.DevBoxProblems) { output.WriteLine($"config.json: {wrong}"); }
                    output.WriteLine(settings.DevBox.KeepAwake
                        ? $"Keep-awake is on (devBox.keepAwake): while the host serves, a pass every {settings.DevBox.IntervalMinutes} min (devBox.intervalMinutes) skips or delays this Dev Box's pending stop-on-disconnect."
                        : "Keep-awake is off (devBox.keepAwake): this host does not keep this Dev Box awake. Turn it on in the app's Settings, or with: agent-bridge-host config set devBox.keepAwake true");
                    output.WriteLine(agentHaBridgeKeepAwake() is { } owner
                        ? $"agent-ha-bridge: {owner.Reason}.{(owner.Known ? "" : " THIS DEV BOX MAY HIBERNATE WHEN DISCONNECTED.")}"
                        : "agent-ha-bridge's own Dev Box keep-awake is not on here.");
                    output.WriteLine($"Each pass is logged in {log}, as lines beginning \"keep-awake\".");
                    return 0;
                }
            case "dry-run":
                {
                    await using var keepAwake = service();
                    var result = await keepAwake.PassAsync(dryRun: true, cancellationToken);
                    // As agent-ha-bridge's pass exits: a failure is 1, a stop that cannot be moved 2.
                    return result.Outcome switch { KeepAwakeResult.Failed => 1, KeepAwakeResult.Blocked => 2, _ => 0 };
                }
            default:
                error.WriteLine("usage: agent-bridge-host devbox status | devbox dry-run   [--profile <name>]");
                return 2;
        }
    }
}
