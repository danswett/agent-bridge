namespace AgentBridge.Roster;

// The text forms of binary values: base64url without padding (keys, hashes, signatures, WebAuthn
// data) and lowercase hex (device ids, generations). Strict both ways: a value has exactly one
// accepted spelling, so nobody but its signer can change an entry's bytes and keep it valid.
public static class Codec
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static string Base64Url(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    // Refuses padding, any character outside the URL-safe alphabet, a length no encoding has, and
    // unused trailing bits that are not zero (a second spelling of the same bytes).
    public static bool TryBase64Url(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 4 == 1) { return false; }
        foreach (var c in text)
        {
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0) { return false; }
        }
        try { bytes = System.Buffers.Text.Base64Url.DecodeFromChars(text); }
        catch (FormatException) { return false; }
        return Base64Url(bytes) == text;
    }

    public static byte[] FromBase64Url(string text, string what) =>
        TryBase64Url(text, out var bytes) ? bytes : throw new RosterFormatException(Refusal.Base64, what);

    // Exactly `length` bytes, or the field's refusal.
    public static byte[] FromBase64Url(string text, int length, string what)
    {
        var bytes = FromBase64Url(text, what);
        return bytes.Length == length ? bytes : throw new RosterFormatException(Refusal.Length, what);
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    // 32 lowercase hex characters: a device id or a generation.
    public static bool IsId(string text)
    {
        if (text.Length != 32) { return false; }
        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) { return false; }
        }
        return true;
    }
}
