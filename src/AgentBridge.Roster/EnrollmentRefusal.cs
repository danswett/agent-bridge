namespace AgentBridge.Roster;

// Why an enrollment is refused (device-approval.md §4.4), as a code on the wire: the `refusal` member of an enrollment answer, the
// same at both ends, so each side can say why. The codes and their order are protocol: devices.md ("Enrollment refusals") lists
// them and the PWA's port (pwa/js/roster/enrollment-refusal.js) is tested against the same table. These eight are what a person is
// told; the host's own step errors (attempt-store, no-attempt, ...) are not on this list and are never shown as a reason.
public static class EnrollmentRefusal
{
    public const string WrongCode = "wrong-code";
    public const string Expired = "expired";
    public const string AlreadyApproved = "already-approved";
    public const string Removed = "removed";
    public const string UnknownKind = "unknown-kind";
    public const string RosterLimit = "roster-limit";
    public const string ApproverProbation = "approver-probation";
    public const string NoPresence = "no-presence";

    public static readonly IReadOnlyList<string> All =
        [WrongCode, Expired, AlreadyApproved, Removed, UnknownKind, RosterLimit, ApproverProbation, NoPresence];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);

    // What a person is told. A code this build does not have gets no words, so a screen shows its generic text.
    public static string Message(string code) => code switch
    {
        WrongCode => "That code is not the one this device shows.",
        Expired => "This attempt ran out of time. Start again.",
        AlreadyApproved => "This device is already approved.",
        Removed => "This device was removed; sign out and in again to make a new key.",
        UnknownKind => "This kind of device cannot be approved.",
        RosterLimit => "This account has the most devices it can have; remove one first.",
        ApproverProbation => "This device is too new to approve others yet.",
        NoPresence => "This device cannot prove that a person is here, so it cannot approve.",
        _ => "",
    };

    // The reason an approver may not approve this joiner against this roster, or null. First match wins, in the order of §4.4:
    // an unknown kind, a device already approved, a key removed before, a roster at its limit, an approver on probation or held,
    // an approver without presence (or not in the roster at all). The wrong code and an expired attempt are the exchange's.
    public static string? Check(RosterVerifier verifier, string approverId, string joinerId, string joinerKind)
    {
        if (!RosterKinds.IsKnown(joinerKind)) { return UnknownKind; }
        var state = verifier.State;
        if (state.Active.ContainsKey(joinerId)) { return AlreadyApproved; }
        for (var i = 0; i < verifier.Graph.Count; i++)
        {
            if (verifier.Graph[i].IsAdd && verifier.Graph[i].DeviceId == joinerId && state.IsRevoked(i)) { return Removed; }
        }
        if (state.ActiveCount >= RosterVerifier.MaxActive) { return RosterLimit; }
        if (state.ActiveAdd(approverId) is not { } add) { return NoPresence; }
        if (verifier.OnProbation(add) || verifier.Held.Contains(approverId)) { return ApproverProbation; }
        return state.Device(approverId)?.Presence is Presence.Key or Presence.WebAuthn ? null : NoPresence;
    }
}
