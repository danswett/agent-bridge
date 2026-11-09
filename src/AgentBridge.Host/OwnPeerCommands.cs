using AgentBridge.Transport;

namespace AgentBridge.Host;

// Local owner commands for AP5-lite's pinned own-peers, run only at this machine's command line
// (Program refuses them inside an agent session; no message or pipe route reaches them).
// Pinning shows the device id and fingerprint and waits for the owner to type yes.
public static class OwnPeerCommands
{
    public const string Usage = "agent-bridge-host peers show | peers list | peers pin <pin-code> [--name <name>] | peers clear [<device-id>]";

    public static bool Writes(IReadOnlyList<string> args) =>
        args.Count > 1 && args[0] == "peers" && args[1] is "pin" or "clear" or "show";

    // This host's own device id and account id (null when not signed in).
    public sealed record Local(string DeviceId, string? Account);

    public static int Run(IReadOnlyList<string> args, string folder, Func<bool> modeOn, Local local, Func<string?> readLine, TextWriter output, TextWriter error)
    {
        try { return Dispatch(args, folder, modeOn, local, readLine, output, error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { error.WriteLine($"Failed: {e.Message}"); return 4; }
    }

    static int Dispatch(IReadOnlyList<string> args, string folder, Func<bool> modeOn, Local local, Func<string?> readLine, TextWriter output, TextWriter error)
    {
        var store = new OwnPeers(folder, modeOn, () => local.Account);
        switch (args.ElementAtOrDefault(1))
        {
            case "clear":
                {
                    var id = args.ElementAtOrDefault(2);
                    if (id is not null && !Wire.IsDeviceId(id)) { error.WriteLine("That is not a device id."); return 2; }
                    var n = store.Clear(id);
                    output.WriteLine(id is null ? $"Cleared {n} pinned peer(s). No peers are trusted." : $"Cleared {n} pinned peer(s).");
                    return 0;
                }
            case "list":
                {
                    if (!store.Enabled) { output.WriteLine("Own-account messaging is off. No peers are trusted."); return 0; }
                    if (store.Damaged) { output.WriteLine("The pinned peer file is damaged, so no peers are trusted. Run `peers clear` to start again."); return 0; }
                    var peers = store.List();
                    if (peers.Count == 0) { output.WriteLine("No peers are pinned, so none are trusted."); }
                    foreach (var p in peers)
                    {
                        output.WriteLine($"{p.DeviceId}  {OwnPeers.Fingerprint(BlobFormat.FromBase64Url(p.PublicKey)!)}  {p.Name}");
                    }
                    return 0;
                }
            case "show":
                {
                    if (!store.Enabled) { error.WriteLine("Own-account messaging is off on this host."); return 3; }
                    if (local.Account is null) { error.WriteLine("This host is not signed in, so it has no account."); return 3; }
                    using var key = AgentSignKey.OpenOrCreate(folder, modeOn: true);
                    output.WriteLine($"device id:    {local.DeviceId}");
                    output.WriteLine($"fingerprint:  {OwnPeers.Fingerprint(key.PublicPoint)}");
                    output.WriteLine($"pin code:     {OwnPeerPin.Make(key, local.DeviceId, local.Account)}");
                    return 0;
                }
            case "pin" when args.Count >= 3:
                {
                    if (!store.Enabled) { error.WriteLine("Own-account messaging is off on this host."); return 3; }
                    if (OwnPeerPin.Parse(args[2]) is not { } pin) { error.WriteLine("That pin code is not valid or is not signed by its key."); return 2; }
                    var name = args.SkipWhile(a => a != "--name").Skip(1).FirstOrDefault() ?? "peer";
                    output.WriteLine($"Pin this host as one of your own?\n  device id:    {pin.DeviceId}\n  fingerprint:  {OwnPeers.Fingerprint(pin.Key)}\n  name:         {name}");
                    output.Write("Check both against the other host's `peers show`, then type yes: ");
                    if (!string.Equals(readLine()?.Trim(), "yes", StringComparison.Ordinal)) { error.WriteLine("Not pinned."); return 1; }
                    try { store.Pin(args[2], name); }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        error.WriteLine($"Not pinned: {e.Message}");
                        return 2;
                    }
                    output.WriteLine("Pinned.");
                    return 0;
                }
            default:
                error.WriteLine("usage: " + Usage);
                return 2;
        }
    }
}
