using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

// Why a reader refused an envelope. The code is the vectors' `refusedBy`, so one defect gives
// one word in every implementation.
public static class Refusal
{
    public const string Json = "json";
    public const string TooLarge = "too-large";
    public const string Duplicate = "duplicate-member";
    public const string Unknown = "unknown-member";
    public const string Missing = "missing-member";
    public const string Type = "wrong-type";
    public const string Base64 = "base64";
    public const string Length = "length";
    public const string Value = "value";
    public const string Version = "version";
    public const string Action = "action";
    public const string Target = "target";
    public const string Chain = "chain";
    public const string Lifetime = "lifetime";
    public const string NonCanonical = "non-canonical";
    public const string Der = "der";
    public const string HighS = "high-s";
    public const string Point = "point";
    public const string Signature = "signature";
    public const string Data = "data";
}

public sealed class EnvelopeException(string code, string where) : Exception($"{code}: {where}")
{
    public string Code { get; } = code;
    public string Where { get; } = where;
}

// The agent-command label and its framing call: the only part of the signing input that is the
// envelope's own. F, Label, base64url and P-256 are the roster library's (#1338), so every verifier
// computes from the same bytes; a roster-label signature is therefore refused as a command's.
public static class AgentCommandFraming
{
    public const string AgentCommandLabel = "agent-bridge agent command v1";

    // What a hostSign key signs for an envelope body.
    public static byte[] AgentCommand(ReadOnlySpan<byte> body) => Framing.F(Framing.Label(AgentCommandLabel), body.ToArray());
}

// The envelope's field checks, which refuse with an EnvelopeException where the roster's raise
// RosterFormatException.
public static class EnvelopeChecks
{
    // `text` must be base64url of exactly `length` bytes.
    public static void CheckBytes(string text, int length, string where)
    {
        if (!Codec.TryBase64Url(text, out var bytes)) { throw new EnvelopeException(Refusal.Base64, where); }
        if (bytes.Length != length) { throw new EnvelopeException(Refusal.Length, where); }
    }

    // An account id is 32 lowercase hex or a GitHub numeric id (1 to 20 ASCII digits, no leading zero so one id has one spelling); every other id stays 32 hex.
    public static bool IsAccountId(string text)
    {
        if (Codec.IsId(text)) { return true; }
        if (text.Length is 0 or > 20 || (text.Length > 1 && text[0] == '0')) { return false; }
        foreach (var c in text)
        {
            if (c is < '0' or > '9') { return false; }
        }
        return true;
    }

    public static void CheckSignatureShape(ReadOnlySpan<byte> der, string where)
    {
        if (P256.ReadDer(der) is not { } rs) { throw new EnvelopeException(Refusal.Der, where); }
        if (!P256.IsLowS(rs.S)) { throw new EnvelopeException(Refusal.HighS, where); }
    }
}