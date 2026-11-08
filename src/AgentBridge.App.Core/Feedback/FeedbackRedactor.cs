using System.Text.RegularExpressions;

namespace AgentBridge.App.Core.Feedback;

// Removes what must never leave the machine from free text: tokens, key material, emails and the
// user's profile path. Replacements are fixed words, so the output never carries a piece of the
// secret, and the same text always gives the same result (the preview is what is sent).
public static partial class FeedbackRedactor
{
    public const string Mark = "[redacted]";

    // Names the machine knows itself by (user, computer) are cut as whole words too, so a surname
    // or host name in free text goes with the profile path.
    public static string Redact(string text, IReadOnlyList<string>? privateNames = null)
    {
        if (string.IsNullOrEmpty(text)) { return text; }
        try
        {
            text = RedactSecrets(text);
            text = Email().Replace(text, Mark);
            text = WindowsProfile().Replace(text, m => m.Groups[1].Value + "<user>");
            text = UncHost().Replace(text, "$1<host>");
            text = UnixProfile().Replace(text, m => m.Groups[1].Value + "<user>");
            if (privateNames is not null)
            {
                foreach (var name in privateNames.Where(n => n is { Length: >= 3 }).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
                }
            }
            return text;
        }
        // A pattern that cannot finish on hostile text must not let the text through unredacted.
        catch (RegexMatchTimeoutException) { return Mark; }
    }

    const int TimeoutMs = 2000;
    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(TimeoutMs);

    // Credentials only, the patterns that must never be sent. S2's pre-send refusal uses these same
    // patterns through ContainsSecret, so there is one list.
    static string RedactSecrets(string text)
    {
        var decoded = DecodeEscapes(text);
        var redacted = Pass(decoded);
        // Text with nothing to redact comes back as it was written, escapes included.
        return string.Equals(redacted, decoded, StringComparison.Ordinal) ? text : redacted;
    }

    // Test seam: lets a test force the timeout path, which real patterns only reach on hostile text.
    [ThreadStatic] internal static Func<string, string>? SecretPassOverride;

    static string Pass(string text) => SecretPassOverride?.Invoke(text) ?? RedactDecoded(text);

    // A journal line can hold JSON with its keys written as \uXXXX, which the patterns would not
    // see as a word; matching runs on the decoded text.
    static string DecodeEscapes(string text) => text.Contains("\\u", StringComparison.Ordinal)
        ? UnicodeEscape().Replace(text, m =>
        {
            // Only characters a credential word is spelled with; quotes, backslashes, control and
            // non-ASCII characters stay escaped so they cannot change where a value ends.
            var c = (char)Convert.ToInt32(m.Groups[1].Value, 16);
            return c < 128 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? c.ToString() : m.Value;
        })
        : text;

    static string RedactDecoded(string text)
    {
        text = PrivateKey().Replace(text, Mark);
        text = Jwt().Replace(text, Mark);
        text = Token().Replace(text, Mark);
        text = Bearer().Replace(text, "Bearer " + Mark);
        text = UrlCredentials().Replace(text, "$1" + Mark + "@");
        text = ConnectionString().Replace(text, m => m.Groups[1].Value + Mark);
        text = Authorization().Replace(text, m => m.Groups[1].Value + Mark);
        // Keeps the key's name so a reader can tell what was removed.
        text = Assignment().Replace(text, m => m.Groups[1].Value + Mark);
        text = CliFlag().Replace(text, m => m.Groups[1].Value + Mark);
        text = LongSecret().Replace(text, Mark);
        return text;
    }

    // True when Redact would change the text.
    public static bool WouldRedact(string text) => !string.Equals(Redact(text), text, StringComparison.Ordinal);

    // True when the text still holds a credential-shaped string.
    public static bool ContainsSecret(string text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        try
        {
            var decoded = DecodeEscapes(text);
            return !string.Equals(Pass(decoded), decoded, StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException) { return true; }
    }

    // Checks every string in a bundle's JSON for a credential. The screenshot is skipped: its base64
    // is long by nature and is not text we read.
    public static bool BundleHasSecret(string bundleJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(bundleJson);
        return Scan(doc.RootElement, false);

        // underCredentialKey carries a credential-named key down into array elements and object
        // members. Booleans and numbers under such a key are refused too: they are odd and safe to refuse.
        static bool Scan(System.Text.Json.JsonElement e, bool underCredentialKey)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    return (underCredentialKey && HoldsValue(e.GetString())) || ContainsSecret(e.GetString()!);
                case System.Text.Json.JsonValueKind.Number:
                case System.Text.Json.JsonValueKind.True:
                case System.Text.Json.JsonValueKind.False:
                    return underCredentialKey;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) { if (Scan(item, underCredentialKey)) { return true; } }
                    return false;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.NameEquals("screenshotPng")) { continue; }
                        if (ContainsSecret(p.Name) || Scan(p.Value, underCredentialKey || IsCredentialName(p.Name))) { return true; }
                    }
                    return false;
                default: return false;
            }
        }

        // Any value under a credential name counts unless it is empty or already redacted, whatever
        // characters it starts with.
        static bool HoldsValue(string? v) => !string.IsNullOrEmpty(v) && !v.Contains(Mark, StringComparison.Ordinal);
    }

    static readonly string[] CredentialNames = ["password", "passwd", "pwd", "secret", "token", "apikey", "accesskey", "authorization", "privatekey", "credential", "credentials", "connectionstring"];

    // Exact names (apiKey, API_KEY, github_token), not substrings: tokenCount and max_tokens are
    // ordinary settings and must not make every send refuse.
    static bool IsCredentialName(string name)
    {
        var n = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return CredentialNames.Any(c => n.EndsWith(c, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY(?: BLOCK)?-----|$)", RegexOptions.None, TimeoutMs)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Token();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<![\w+.-])([a-z][a-z0-9+.-]{0,31}://)[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex UrlCredentials();

    // A connection string holds several secrets between its semicolons, so the whole line goes.
    [GeneratedRegex(@"(\bconnection[_ -]?string[\w.-]{0,64}[""']?\s*[:=]\s*)(?!\s*\[redacted\])[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"(?<![\\:\w])(\\{4}|\\{2})[^\\\s""'/]+(?=\\)", RegexOptions.None, TimeoutMs)]
    private static partial Regex UncHost();

    [GeneratedRegex(@"(\bauthorization[""']?\s*[:=]\s*)(?!\s*\[redacted\])(?:[A-Za-z]+\s+)?[^\s"",;}\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Authorization();

    // The value is a quoted string to its closing quote (also when the JSON is escaped inside a log
    // line) or one bare token.
    [GeneratedRegex(@"((?<![\w.-])[\w.-]{0,64}(?:password|passwd|pwd|secret|token|api[_ -]?key|authorization|private[_ -]?key|credential|connection[_ -]?string)[\w.-]{0,64}(?:\\*[""'])?\s*[:=]\s*)(?!\s*\[redacted\])(?:\\*""[^""\r\n]*""|'[^'\r\n]*'|(?:\\*[""'])?(?:[^\s""',;}&\\]|\\(?![""']))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Assignment();

        [GeneratedRegex(@"\\u([0-9a-fA-F]{4})", RegexOptions.None, TimeoutMs)]
        private static partial Regex UnicodeEscape();

    [GeneratedRegex(@"(?<![\w-])(--?(?:[a-z0-9]{1,20}[-_]){0,3}(?:password|passwd|pwd|token|secret|api-?key)\s+)(?!\s*\[redacted\])(?!-)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CliFlag();

    [GeneratedRegex(@"(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.None, TimeoutMs)]
    private static partial Regex Email();

    [GeneratedRegex(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/""'\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex WindowsProfile();

    [GeneratedRegex(@"(/(?:home|Users)/)(?!<user>)[^/\s""']+", RegexOptions.None, TimeoutMs)]
    private static partial Regex UnixProfile();

    // Base64 or hex runs long enough to be key material rather than a word, a hash is shorter.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])(?:[A-Fa-f0-9]{64,}|[A-Za-z0-9+/_-]{48,}={0,2})(?![A-Za-z0-9+/=_-])", RegexOptions.None, TimeoutMs)]
    private static partial Regex LongSecret();
}
