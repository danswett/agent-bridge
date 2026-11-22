using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

public sealed record DeliverySender(string Id, string? Display = null, bool Owner = false)
{
    public string From => (Owner ? "owner:" : "agent:") + Uri.EscapeDataString(Id) +
        (Display is { Length: > 0 and <= 48 } && Regex.IsMatch(Display, @"\A[A-Za-z0-9 ._-]+\z", RegexOptions.CultureInvariant)
            ? " (" + Display + ")" : "");
}

public sealed record DeliveryHeader(string From, string Id, string? Seq, DateTimeOffset At, string Priority, int N);
public sealed record DeliveryEntry(string Text, DeliveryHeader? Header = null, bool LengthMismatch = false);

public static class DeliveryEnvelope
{
    static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    static readonly Regex Header = new(@"\[ab:from=(system|(?:owner|agent):[A-Za-z0-9%._~-]+(?: \([A-Za-z0-9 ._-]{1,48}\))?)\|id=([A-Za-z0-9_.-]+)\|seq=([uq][1-9][0-9]*)\|at=([^|\]\r\n]+)\|pri=(urgent|normal|fyi)\|n=([0-9]+)\] ",
        RegexOptions.CultureInvariant);
    static readonly Regex Boundary = new(@"(?:\A|(?<=\n\n))(?=\[ab:)", RegexOptions.CultureInvariant);
    static readonly Regex Marker = new(@"\[ab:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (DeliveryHeader Header, string Body) Prepare(DeliveryHeader header, string text)
    {
        var body = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) { continue; }
            if (Rune.IsWhiteSpace(rune))
            {
                if (body.Length > 0 && body[^1] != ' ') { body.Append(' '); }
            }
            else if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Control) { body.Append(rune); }
        }
        var words = Marker.Replace(body.ToString().TrimEnd(), m => "(" + m.Value[1..]);
        var at = TimeZoneInfo.ConvertTime(header.At, Pacific);
        at = new DateTimeOffset(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, at.Offset);
        return (header with { At = at, N = words.Length }, words);
    }

    public static string Format(DeliveryHeader header, string text)
    {
        var (h, body) = Prepare(header, text);
        var prefix = string.Create(CultureInfo.InvariantCulture, $"[ab:from={h.From}|id={h.Id}|seq={h.Seq}|at={h.At:yyyy-MM-ddTHH:mm:sszzz}|pri={h.Priority}|n={h.N}] ");
        if (!TryHeader(prefix, 0, out _, out var end) || end != prefix.Length)
        {
            throw new ArgumentException("Invalid delivery envelope metadata.", nameof(header));
        }
        return prefix + body;
    }

    // Only the host's session-bound journal verifier may supply a positive verdict.
    public static IReadOnlyList<DeliveryEntry> Parse(string session, string turn,
        Func<string, DeliveryHeader, string, bool>? verifyHandoff = null)
    {
        var entries = new List<DeliveryEntry>();
        var console = new StringBuilder();
        var position = 0;
        foreach (Match boundary in Boundary.Matches(turn))
        {
            if (boundary.Index < position) { continue; }
            console.Append(turn.AsSpan(position, boundary.Index - position));
            position = boundary.Index;
            if (!TryHeader(turn, position, out var h, out var bodyStart)) { continue; }
            var nextBlank = turn.IndexOf("\n\n", bodyStart, StringComparison.Ordinal);
            var available = (nextBlank < 0 ? turn.Length : nextBlank) - bodyStart;
            var mismatch = h.N > available;
            var end = bodyStart + (mismatch ? available : h.N);
            var body = turn[bodyStart..end];
            if (!mismatch && verifyHandoff?.Invoke(session, h, body) == true)
            {
                if (console.Length > 0) { entries.Add(new(console.ToString())); console.Clear(); }
                entries.Add(new(body, h));
            }
            else if (mismatch)
            {
                console.Append(turn.AsSpan(position, end - position));
                entries.Add(new(console.ToString(), LengthMismatch: true));
                console.Clear();
            }
            else { console.Append(turn.AsSpan(position, end - position)); }
            position = end;
        }
        console.Append(turn.AsSpan(position));
        if (console.Length > 0) { entries.Add(new(console.ToString())); }
        return entries;
    }

    static bool TryHeader(string text, int start, out DeliveryHeader header, out int end)
    {
        header = null!;
        end = start;
        var match = Header.Match(text, start);
        if (!match.Success || match.Index != start ||
            !DateTimeOffset.TryParseExact(match.Groups[4].Value, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at) ||
            !int.TryParse(match.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { return false; }
        header = new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, at, match.Groups[5].Value, n);
        end = match.Index + match.Length;
        return true;
    }
}
