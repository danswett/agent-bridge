using System.Globalization;
using System.Text;
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
        catch (ArgumentException) { return Mark; }
    }

    internal static string RedactLogLines(string text, IReadOnlyList<string>? privateNames = null)
    {
        try
        {
            text = PrivateKey().Replace(text, Mark);
            return string.Join('\n', text.Split('\n').Select(line => Redact(line.TrimEnd('\r'), privateNames)));
        }
        catch (RegexMatchTimeoutException) { return Mark; }
    }

    const int TimeoutMs = 2000;
    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(TimeoutMs);

    // Credentials only, the patterns that must never be sent. S2's pre-send refusal uses these same
    // patterns through ContainsSecret, so there is one list.
    // Two views of each text: as written, and with every escape decoded. A credential found only in
    // the decoded view (nested JSON, \u0022 quotes, \u003D) cannot be cut by span, so the whole text
    // is replaced.
    static string RedactSecrets(string text)
    {
        if (UnsafeCredentialMention(text) || HiddenSecret(text)) { return EscapedMark; }
        var redacted = Pass(text);
        return HiddenSecret(redacted) ? EscapedMark : redacted;
    }

    static bool UnsafeCredentialMention(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (string.Equals(line.TrimEnd('\r'), EscapedMark, StringComparison.Ordinal)) { continue; }
            if (CredentialMention().IsMatch(line)) { return true; }
            if (line.Any(c => c > 127) && CredentialMention().IsMatch(NormalizeUnicode(line))) { return true; }
        }
        return false;
    }

    static string NormalizeUnicode(string text)
    {
        var wellFormed = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
        var normalized = wellFormed.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
        var view = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format))
            {
                view.Append(rune);
            }
        }
        return view.ToString();
    }

    internal static bool MentionsSensitiveField(string text)
    {
        try
        {
            var view = text;
            for (var pass = 0; pass <= 4; pass++)
            {
                if (SensitiveFieldMention().IsMatch(view)
                    || (view.Any(c => c > 127) && SensitiveFieldMention().IsMatch(NormalizeUnicode(view)))) { return true; }
                var next = Normalize(view);
                if (string.Equals(next, view, StringComparison.Ordinal)) { return false; }
                view = next;
            }
            return EscapeSequence().IsMatch(view);
        }
        catch (RegexMatchTimeoutException) { return true; }
        catch (ArgumentException) { return true; }
    }

    // Decodes again and again (a key can be escaped twice: \u005cu0070assword) and checks each view.
    // Escapes still left after the cap fail closed.
    static bool HiddenSecret(string text)
    {
        var view = text;
        for (var pass = 0; pass < 4; pass++)
        {
            var next = Normalize(view);
            if (string.Equals(next, view, StringComparison.Ordinal)) { return false; }
            if (UnsafeCredentialMention(next) || HasSecretIn(next)) { return true; }
            view = next;
        }
        return EscapeSequence().IsMatch(view);
    }

    public const string EscapedMark = "[redacted: escaped secret]";

    static bool HasSecretIn(string text) => !string.Equals(Pass(text), text, StringComparison.Ordinal);

    // Test seam: lets a test force the timeout path, which real patterns only reach on hostile text.
    [ThreadStatic] internal static Func<string, string>? SecretPassOverride;

    static string Pass(string text) => SecretPassOverride?.Invoke(text) ?? RedactDecoded(text);

    static string Normalize(string text) => text.Contains('\\')
        ? EscapeSequence().Replace(text, m => m.Groups[1].Success
            ? ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()
            : m.Groups[2].Success
                ? ((char)Convert.ToInt32(m.Groups[2].Value, 16)).ToString()
                : m.Groups[3].Value switch { "n" => "\n", "r" => "\r", "t" => "\t", var c => c })
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
            return UnsafeCredentialMention(text) || HasSecretIn(text) || HiddenSecret(text);
        }
        catch (RegexMatchTimeoutException) { return true; }
        catch (ArgumentException) { return true; }
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
            if (underCredentialKey)
            {
                return e.ValueKind != System.Text.Json.JsonValueKind.String ||
                    e.GetString() is not (Mark or EscapedMark);
            }
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    return ContainsSecret(e.GetString()!);
                case System.Text.Json.JsonValueKind.Number:
                case System.Text.Json.JsonValueKind.True:
                case System.Text.Json.JsonValueKind.False:
                    return false;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) { if (Scan(item, underCredentialKey)) { return true; } }
                    return false;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.NameEquals("screenshotPng")) { continue; }
                        var credentialKey = IsCredentialName(p.Name);
                        if (WouldRedact(p.Name) || Scan(p.Value, credentialKey)) { return true; }
                    }
                    return false;
                default: return false;
            }
        }
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

    const string SensitiveFieldPattern = @"(?:password|passwd|pwd|secret|token|api[_ -]?key|access[_ -]?key|authorization|private[_ -]?key|credentials?|connection[_ -]?string)";

    [GeneratedRegex(SensitiveFieldPattern + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CredentialMention();

    [GeneratedRegex(SensitiveFieldPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SensitiveFieldMention();

    [GeneratedRegex(@"\\(?:u([0-9a-fA-F]{4})|x([0-9a-fA-F]{2})|([""'\\/nrt]))", RegexOptions.None, TimeoutMs)]
    private static partial Regex EscapeSequence();

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
