using AgentBridge.Protocol;
using AgentBridge.Roster;

namespace AgentBridge.App.Core;

// Same-PC trust (#3450 ruling item 3): what the signed-in app hands the host beside it, and only
// when the roster it verified holds this app as an active person device past probation. No prompt
// and no typed value: the person sees one status line. Self-contained, so host tests compile it too.
public static class SamePcPin
{
    public static (SamePcPinRequest? Request, string? Refusal) Offer(RosterVerifier? verified, string? signedInAccount, string? ownDevice)
    {
        if (string.IsNullOrEmpty(signedInAccount) || string.IsNullOrEmpty(ownDevice)) { return (null, "not-signed-in"); }
        if (verified is null || verified.Graph.Count == 0 || verified.Graph[0].Op != EntryOp.Genesis) { return (null, "unverified"); }
        if (verified.Account != signedInAccount) { return (null, "other-account"); }
        var state = verified.State;
        if (state.ActiveAdd(ownDevice) is not { } add || state.Device(ownDevice)?.IsPerson != true) { return (null, "not-person-device"); }
        if (verified.OnProbation(add) || verified.Held.Contains(ownDevice)) { return (null, "probation"); }
        return (new(verified.Account, verified.Generation, Codec.Base64Url(verified.Graph[0].Hash),
            [.. verified.Heads.Select(h => Codec.Base64Url(h))], ownDevice), null);
    }

    // The host's closed answer as the one line the person sees; null when there is nothing to say.
    public static string? Line(string state) => state switch
    {
        SamePcPinProtocol.Pinned => "This PC is now trusted for fleet messaging",
        "pin-exists" => null,
        _ => $"This PC could not be trusted for fleet messaging ({state})",
    };

    // callLocal must be this machine's own pipe connection, never a relay one.
    public static async Task<string?> HandOffAsync(Func<string, object, Task<SamePcPinResult>> callLocal,
        RosterVerifier? verified, string? signedInAccount, string? ownDevice)
    {
        var (request, refusal) = Offer(verified, signedInAccount, ownDevice);
        if (request is null) { return refusal == "not-signed-in" ? null : Line(refusal!); }
        try { return Line((await callLocal(SamePcPinProtocol.Op, request)).State); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or AgentBridge.Local.HostErrorException) { return Line("host-unreachable"); }
    }
}
