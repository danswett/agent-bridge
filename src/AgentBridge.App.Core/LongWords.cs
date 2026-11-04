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

    // Where a paragraph's text takes a break opportunity, so that it never has more than max
    // characters without one. Not the line-breaking rules, which a space does not always satisfy
    // (none after `[`, even past spaces), but a count, cut where it costs least: just after the last
    // space, where a line could already end, else between grapheme clusters, so an accent stays on
    // its letter. Only a cluster longer than max is cut inside it, never inside a surrogate pair:
    // one mark drawn on its own beats the app ending. A line break starts the count again.
    public static IReadOnlyList<int> Cuts(string text, int max = Max)
    {
        var cuts = new List<int>();
        if (text.Length <= max) { return cuts; }
        int since = 0, last = 0, afterSpace = -1;
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
                var cut = afterSpace > last && i - afterSpace + n <= max ? afterSpace : i;
                cuts.Add(cut);
                since = i - cut;
                last = cut;
                afterSpace = -1;
            }
            for (var end = i + n; end - i > max;)
            {
                var cut = i + max;
                if (char.IsLowSurrogate(text[cut]) && char.IsHighSurrogate(text[cut - 1])) { cut--; }
                cuts.Add(cut);
                last = i = cut;
                n = end - i;
            }
            since += n;
            i += n;
            if (n == 1 && text[i - 1] is ' ' or '\t') { afterSpace = i; }
        }
        return cuts;
    }

    // A paragraph's runs (a bold word straight after a plain one is one word on screen), each in
    // the pieces that join to exactly it, with a break opportunity between each two. Cut as their
    // joined text is (Cuts), so a cluster split across two runs is still never cut inside. A first
    // piece of "" puts a break before the run.
    public static IReadOnlyList<IReadOnlyList<string>> Pieces(IReadOnlyList<string> runs, int max = Max)
    {
        var text = string.Concat(runs);
        var cuts = Cuts(text, max);
        var pieces = new List<IReadOnlyList<string>>(runs.Count);
        int at = 0, next = 0;
        foreach (var run in runs)
        {
            var end = at + run.Length;
            if (next >= cuts.Count || cuts[next] >= end)
            {
                pieces.Add([run]);
                at = end;
                continue;
            }
            var own = new List<string>();
            var start = at;
            if (cuts[next] == at) { own.Add(""); next++; }
            for (; next < cuts.Count && cuts[next] < end; next++)
            {
                own.Add(text[start..cuts[next]]);
                start = cuts[next];
            }
            own.Add(text[start..end]);
            pieces.Add(own);
            at = end;
        }
        return pieces;
    }
}
