using AgentBridge.Roster;

namespace AgentBridge.App.Core;

// Device approval, the app's side (#55, docs/design/device-approval.md section 4 and slice R4). View models only: the exchange, its codes and
// its lockouts are the roster library's (EnrollmentApprover, EnrollmentJoiner, EnrollmentLockout), wrapped here without any crypto of
// their own. Nothing here reaches a relay or a host; the wiring and the screens are later slices.

// The roster mode of the account as the relay's roster snapshot says it (observe or enforce). Anything else, including no answer, is off:
// no join or approve entry exists and nothing behind it is built.
public interface IRosterModeSource { string? Mode { get; } }

public static class DeviceApprovalGate
{
    public static bool Visible(string? mode) => mode is "observe" or "enforce";

    public static T? Open<T>(IRosterModeSource source, Func<T> make) where T : class => Visible(source.Mode) ? make() : null;
}

// What proves a person is present at this device (section 4.6), as the roster's own Presence values; a Kind that is neither Key nor
// WebAuthn is no presence at all. The Windows app has none yet: R4 must first prove that same-user code (SendInput, UI Automation, this
// project's injector) cannot satisfy its mechanism, so approving from it is not possible until then.
public interface IPresenceProof
{
    string Kind { get; }
    bool TrySign(byte[] message, out byte[] signature);
}

public sealed class NoPresence : IPresenceProof
{
    public string Kind => Presence.None;
    public bool TrySign(byte[] message, out byte[] signature) { signature = []; return false; }
}

// The fixed words the screens use. No text from a peer, the relay or an exception is ever shown.
public static class EnrollmentTexts
{
    public const string Wrong = "That code is not right. Try again.";
    public const string Ended = "This attempt has ended. Start again.";
    public const string Locked = "Joining is locked for 15 minutes.";
    public const string Generic = "This device could not be added.";

    static readonly Dictionary<string, string> Refusals = new(StringComparer.Ordinal)
    {
        ["wrong-code"] = Wrong,
        ["expired"] = "This attempt ran out of time. Start again.",
        ["already-approved"] = "This device is already approved.",
        ["removed"] = "This device was removed; sign out and in again to make a new key.",
        ["unknown-kind"] = "This kind of device cannot join.",
        ["roster-limit"] = "The account has reached its device limit.",
        ["approver-probation"] = "The approving device is still on probation.",
        ["no-presence"] = "The approving device cannot prove a person is present.",
    };

    // A closed code to its words; anything else, or nothing, is the generic line.
    public static string ForRefusal(string? code) => code is not null && Refusals.TryGetValue(code, out var text) ? text : Generic;

    public static string ForResult(CodeResult result) => result switch
    {
        CodeResult.Wrong => Wrong,
        CodeResult.Locked => Locked,
        CodeResult.Ended => Ended,
        _ => "",
    };

    // Hex in groups of four: the first 64 bits of a hash, or an id's first sixteen characters.
    public static string Groups(string hex) => string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));

    public static string Fingerprint(byte[] genesisHash) => Groups(Convert.ToHexStringLower(genesisHash.AsSpan(0, 8)));
}
