namespace AgentBridge.Host.Delivery.Copilot;

// Whether words typed into a Copilot prompt show on its screen yet (#467). Read by their end,
// with white space and box-drawing characters left out on both sides: the CLI wraps a long
// answer wherever it likes, and may draw a border down either side of a text field, so the
// words are found however their rows were split.
public static class WordsOnScreen
{
    // The end of the words that is looked for: long enough not to turn up by chance, short
    // enough to stay on screen when the field shows only the end of a long answer.
    const int TailLength = 16;

    public static string Tail(string words)
    {
        var dense = Dense(words);
        return dense.Length <= TailLength ? dense : dense[^TailLength..];
    }

    // How often the tail shows on these rows.
    public static int Count(IReadOnlyList<string> rows, string tail)
    {
        if (tail.Length == 0) { return 0; }
        var screen = Dense(string.Concat(rows));
        var count = 0;
        for (var at = screen.IndexOf(tail, StringComparison.Ordinal); at >= 0; at = screen.IndexOf(tail, at + tail.Length, StringComparison.Ordinal)) { count++; }
        return count;
    }

    // How often the tail shows in each of these runs of rows, each read on its own: the rows of
    // the field's input (CopilotFormScreen.Input), never joined to a title or a hint beside it.
    public static int Count(IEnumerable<IReadOnlyList<string>> runs, string tail) => runs.Sum(run => Count(run, tail));

    static string Dense(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not (>= '\u2500' and <= '\u257F')));
}
