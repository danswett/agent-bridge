using System.Text.RegularExpressions;

namespace AgentBridge.App.Core.Feedback;

// Removes what must never leave the machine from free text: tokens, key material, emails and the
// user's profile path. Replacements are fixed words, so the output never carries a piece of the
// secret, and the same text always gives the same result (the preview is what is sent).
public static partial class FeedbackRedactor
{
    public const string Mark = "[redacted]";

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) { return text; }
        text = PrivateKey().Replace(text, Mark);
        text = Jwt().Replace(text, Mark);
        text = Token().Replace(text, Mark);
        text = Bearer().Replace(text, "Bearer " + Mark);
        text = UrlCredentials().Replace(text, "$1" + Mark + "@");
        // Keeps the key's name so a reader can tell what was removed.
        text = Assignment().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mark);
        text = Email().Replace(text, Mark);
        text = WindowsProfile().Replace(text, m => m.Groups[1].Value + "<user>");
        text = UnixProfile().Replace(text, m => m.Groups[1].Value + "<user>");
        text = LongSecret().Replace(text, Mark);
        return text;
    }

    // True when Redact would change the text.
    public static bool WouldRedact(string text) => !string.Equals(Redact(text), text, StringComparison.Ordinal);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,}|sk-[A-Za-z0-9_-]{20,})", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://)[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(\b[\w.-]*(?:password|passwd|secret|token|api[_ -]?key|authorization|private[_ -]?key|credential|connection[_ -]?string)[\w.-]*[""']?)(\s*[:=]\s*[""']?)(?!\[redacted\])[^\s""',;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsProfile();

    [GeneratedRegex(@"((?:^|[\s""'=:(])/(?:home|Users)/)[^/\s""']+")]
    private static partial Regex UnixProfile();

    // Base64 or hex runs long enough to be key material rather than a word, a hash is shorter.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/=_-])(?:[A-Fa-f0-9]{64,}|[A-Za-z0-9+/_-]{48,}={0,2})(?![A-Za-z0-9+/=_-])")]
    private static partial Regex LongSecret();
}
