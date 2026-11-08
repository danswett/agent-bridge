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
        text = RedactSecrets(text);
        text = Email().Replace(text, Mark);
        text = UncHost().Replace(text, "$1<host>");
        text = WindowsProfile().Replace(text, m => m.Groups[1].Value + "<user>");
        text = UnixProfile().Replace(text, m => m.Groups[1].Value + "<user>");
        if (privateNames is not null)
        {
            foreach (var name in privateNames.Where(n => n is { Length: >= 3 }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        return text;
    }

    // Credentials only, the patterns that must never be sent. S2's pre-send refusal uses these same
    // patterns through ContainsSecret, so there is one list.
    static string RedactSecrets(string text)
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
    public static bool ContainsSecret(string text) => !string.IsNullOrEmpty(text) && !string.Equals(RedactSecrets(text), text, StringComparison.Ordinal);

    // Checks every string in a bundle's JSON for a credential. The screenshot is skipped: its base64
    // is long by nature and is not text we read.
    public static bool BundleHasSecret(string bundleJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(bundleJson);
        return Scan(doc.RootElement);

        static bool Scan(System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String: return ContainsSecret(e.GetString()!);
                case System.Text.Json.JsonValueKind.Array: return e.EnumerateArray().Any(Scan);
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.NameEquals("screenshotPng")) { continue; }
                        if (ContainsSecret(p.Name) || Scan(p.Value)) { return true; }
                    }
                    return false;
                default: return false;
            }
        }
    }
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY(?: BLOCK)?-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY(?: BLOCK)?-----|$)")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://)[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    // A connection string holds several secrets between its semicolons, so the whole line goes.
    [GeneratedRegex(@"(\bconnection[_ -]?string[\w.-]*[""']?\s*[:=]\s*)(?!\[redacted\])[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"(\\\\)[^\\\s""'/]+(?=\\)")]
    private static partial Regex UncHost();

    [GeneratedRegex(@"(\bauthorization[""']?\s*[:=]\s*)(?!\[redacted\])(?:[A-Za-z]+\s+)?[^\s"",;}\\]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Authorization();

    // The value is a quoted string to its closing quote (also when the JSON is escaped inside a log
    // line) or one bare token.
    [GeneratedRegex(@"(\b[\w.-]*(?:password|passwd|secret|token|api[_ -]?key|authorization|private[_ -]?key|credential|connection[_ -]?string)[\w.-]*(?:\\*[""'])?\s*[:=]\s*)(?!\[redacted\])(?:\\*""[^""\r\n]*""|'[^'\r\n]*'|(?:[^\s""',;}&\\]|\\(?![""']))+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(?<![\w-])(--?(?:password|passwd|pwd|token|secret|api-?key)\s+)(?!\[redacted\])(?!-)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliFlag();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/""'\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsProfile();

    [GeneratedRegex(@"(/(?:home|Users)/)(?!<user>)[^/\s""']+")]
    private static partial Regex UnixProfile();

    // Base64 or hex runs long enough to be key material rather than a word, a hash is shorter.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])(?:[A-Fa-f0-9]{64,}|[A-Za-z0-9+/_-]{48,}={0,2})(?![A-Za-z0-9+/=_-])")]
    private static partial Regex LongSecret();
}
