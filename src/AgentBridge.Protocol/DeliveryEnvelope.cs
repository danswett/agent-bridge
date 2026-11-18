using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

public enum DeliveryPriority { Normal, Fyi, Urgent }
public sealed record DeliveryEnvelopePart(DeliveryEnvelope? Envelope, string Text);

// Parsing is framing only: a pasted header is NOT authenticated attribution or delivery evidence.
public sealed record DeliveryEnvelope(string Sender, Guid Id, string Sequence, DateTimeOffset At, DeliveryPriority Priority, string Text)
{
    const string SenderPattern = @"(?:owner|system|agent(?::[A-Za-z0-9 ._-]{1,48})?)";
    const string Stamp = "yyyy-MM-dd'T'HH:mm:sszzz";
    static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    static readonly Regex Header = new(@"(?:\A|(?<=\n\n))\[ab:from=(?<from>" + SenderPattern
        + @")\|id=(?<id>[0-9a-f]{32})\|seq=(?<seq>[uq][1-9][0-9]*)\|at=(?<at>[^|\r\n]+)"
        + @"\|pri=(?<pri>urgent|normal|fyi)\|n=(?<n>[0-9]{1,9})\] ", RegexOptions.CultureInvariant);

    public string Format()
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (Sender is null || !Regex.IsMatch(Sender, @"\A" + SenderPattern + @"\z") || Id == Guid.Empty
            || Sequence is null || !Regex.IsMatch(Sequence, @"\A[uq][1-9][0-9]*\z")
            || !Enum.IsDefined(Priority) || Sequence[0] == 'u' != (Priority == DeliveryPriority.Urgent))
        { throw new ArgumentException("invalid delivery envelope metadata"); }
        var clean = Regex.Replace(Text.Normalize(NormalizationForm.FormKC), @"\p{Cf}", "");
        var body = Regex.Replace(clean, @"[\p{Cc}\s]+", " ").Trim().Replace('[', '(');
        var at = TimeZoneInfo.ConvertTime(At, Pacific).ToString(Stamp, CultureInfo.InvariantCulture);
        return $"[ab:from={Sender}|id={Id:N}|seq={Sequence}|at={at}|pri={Priority.ToString().ToLowerInvariant()}|n={body.Length.ToString(CultureInfo.InvariantCulture)}] {body}";
    }

    public static IReadOnlyList<DeliveryEnvelopePart> ParseUnverified(string turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var parts = new List<DeliveryEnvelopePart>();
        var cursor = 0;
        foreach (Match match in Header.Matches(turn))
        {
            if (match.Index < cursor) { continue; }
            var g = match.Groups;
            var start = match.Index + match.Length;
            if (!int.TryParse(g["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length > turn.Length - start
                || !DateTimeOffset.TryParseExact(g["at"].Value, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                || !Guid.TryParseExact(g["id"].Value, "N", out var id) || id == Guid.Empty) { continue; }
            var body = turn.Substring(start, length);
            if (!WellFormed(body)) { continue; }
            var envelope = new DeliveryEnvelope(g["from"].Value, id, g["seq"].Value, at,
                Enum.Parse<DeliveryPriority>(g["pri"].Value, ignoreCase: true), body);
            if (envelope.Sequence[0] == 'u' != (envelope.Priority == DeliveryPriority.Urgent)
                || envelope.Format() != match.Value + body) { continue; }
            KeepConsole(match.Index, separator: parts.Count > 0 && parts[^1].Envelope is not null);
            parts.Add(new(envelope, body));
            cursor = start + length;
        }
        KeepConsole(turn.Length);
        return parts;

        void KeepConsole(int end, bool separator = false)
        {
            if (end > cursor && turn[cursor..end] is var text && !(separator && text == "\n\n")) { parts.Add(new(null, text)); }
        }
    }

    static bool WellFormed(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune)) { return false; }
            i += rune.Utf16SequenceLength;
        }
        return true;
    }
}
