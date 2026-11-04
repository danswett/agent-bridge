using System.Globalization;

namespace AgentBridge.App.Core;

// WinUI's RichTextBlock fails to measure a word of about 32,000 characters that follows a place it
// could break (#493): "Done: " and 32,000 `[` threw E_FAIL from Measure, and the app ended with
// 0xC000027B, which no handler catches. Measured in the app's reading role at 700 px: after
// "Done: ", 31,800 `[` drew and 32,000 failed, 31,600 `W` drew and 32,000 failed, and 16,000
// letters each with a combining accent (32,000 characters) failed, so the limit is in characters,
// not width. Splitting the text into several Runs alone does not help; a break opportunity of no
// width between them (MarkdownView's) does. A TextBlock (code, your message) drew 40,000.
public static class LongWords
{
    // A quarter of the measured limit, and half the protocol's 16 KiB response.
    public const int Max = 8192;

    // The text in pieces that join to exactly the text, for a break opportunity between each two:
    // never more than max characters without one. Not the line-breaking rules, which a space does
    // not always satisfy (none after `[`, even past spaces), but a count, cut where it costs least:
    // just after the last space, where a line could already end, else between grapheme clusters, so
    // an accent stays on its letter. Only a cluster longer than max is cut inside it, never inside
    // a surrogate pair: one mark drawn on its own beats the app ending.
    // since: the characters drawn since the last break opportunity, from the runs before this one
    // in its paragraph (a bold word straight after a plain one is one word on screen), 0 after a
    // line break; left at the count the text ends on. A first piece of "" puts a break before it.
    public static IReadOnlyList<string> Pieces(string text, ref int since, int max = Max)
    {
        if (since + text.Length <= max && text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            since += text.Length;
            return [text];
        }
        var pieces = new List<string>();
        int start = 0, afterSpace = -1;
        for (var i = 0; i < text.Length;)
        {
            var n = StringInfo.GetNextTextElementLength(text.AsSpan(i));
            if (text[i] is '\r' or '\n')
            {
                since = 0;
                afterSpace = -1;
                i += n;
                continue;
            }
            if (since > 0 && since + n > max)
            {
                var cut = afterSpace > start && i - afterSpace + n <= max ? afterSpace : i;
                if (cut > start || pieces.Count == 0) { pieces.Add(text[start..cut]); }
                since = i - cut;
                start = cut;
                afterSpace = -1;
            }
            for (var end = i + n; end - i > max;)
            {
                var cut = i + max;
                if (char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1])) { cut--; }
                pieces.Add(text[start..cut]);
                start = i = cut;
                n = end - i;
            }
            since += n;
            i += n;
            if (n == 1 && text[i - 1] is ' ' or '\t') { afterSpace = i; }
        }
        pieces.Add(text[start..]);
        return pieces;
    }
}
