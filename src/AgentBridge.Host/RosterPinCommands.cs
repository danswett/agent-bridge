using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal sealed record PinFetch(RelayRoster? Roster, string? Account, string? RelayAccount);

// agent-bridge-host roster-pin set|show [--profile <name>]: the fleet operator's first-host pin
// (#3450, brief 182; docs/runbooks/roster-pin.md). set is a person at this machine's own
// terminal typing the fingerprint they compared out of band; show reads back only what is pinned.
internal static class RosterPinCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string root, Func<Caller> caller, bool credential, Func<string?>? typed,
        Func<string, CancellationToken, Task<PinFetch>> fetch, Func<TimeSpan> clock, TextWriter output, TextWriter error,
        CancellationToken ct = default)
    {
        var (verb, profile) = args switch
        {
            ["roster-pin", var v] => (v, "default"),
            ["roster-pin", var v, "--profile", var name] => (v, name),
            _ => ("", ""),
        };
        int Say(TextWriter to, int exit, object state) { to.WriteLine(JsonSerializer.Serialize(state, ProtocolJson.Options)); return exit; }
        if (verb is not ("set" or "show")) { return Say(error, 2, new { state = "invalid-command" }); }
        if (!Identity.IsValidProfile(profile)) { return Say(error, 2, new { state = "invalid-profile" }); }
        var pinPath = HostRosterPinWriter.PinPath(Path.Combine(root, profile));
        if (verb == "show")
        {
            var (account, genesis, problem) = HostRosterReader.ReadPinned(pinPath);
            return genesis is null
                ? Say(output, 1, new { state = problem ?? "unpinned" })
                : Say(output, 0, new { state = "pinned", account, fingerprint = HostRosterPinWriter.Fingerprint(genesis) });
        }

        // Trust is a person's decision (roster-v2-transition.md, first-host bootstrap): never an
        // agent, the app's pipe, a script holding a host credential or piped input.
        if (credential || caller().Kind != CallerKind.HostCli) { return Say(error, 4, new { state = "refused" }); }
        if (File.Exists(pinPath)) { return Say(error, 1, new { state = "pin-exists" }); }
        if (typed is null) { return Say(error, 2, new { state = "not-interactive" }); }
        error.Write("Genesis fingerprint you compared out of band (16 hex digits): ");
        if (PersonGenesis.FromFingerprint(typed()) is not { } source) { return Say(error, 2, new { state = "fingerprint-format" }); }
        PinFetch fetched;
        try { fetched = await fetch(profile, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return Say(error, 1, new { state = "relay-unreachable" }); }
        if (fetched.Account is null) { return Say(error, 1, new { state = "no-account" }); }
        if (fetched.Account != fetched.RelayAccount) { return Say(error, 1, new { state = "account-mismatch" }); }
        var result = HostRosterPinWriter.Write(pinPath, source, fetched.Roster, fetched.Account, clock);
        return result == "pinned"
            ? Say(output, 0, new { state = result, account = fetched.Account, fingerprint = HostRosterPinWriter.Fingerprint(source.Expected) })
            : Say(error, 1, new { state = result });
    }
}
