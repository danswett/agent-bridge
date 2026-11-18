namespace AgentBridge.Host.Delivery.Copilot;

public static partial class CopilotInputBoxReader
{
    static bool MatchesOwnedContent(CopilotInputBoxState state, string words)
    {
        if (string.Equals(words.Trim(' '), state.Content, StringComparison.Ordinal)) { return true; }
        // Only ASCII has a known one-cell width here; unknown wrapped rendering stays unowned.
        if (state.Lines < 2 || words.Any(c => c is < ' ' or > '~')) { return false; }

        var bottom = state.Rows[^1].Text.TrimEnd(' ');
        var left = bottom.Length - bottom.TrimStart(' ').Length;
        var columns = bottom.Length - left;
        if (columns < CompactWidth) { return false; }
        var rail = bottom[left] == RailBottom;
        var indent = new string(' ', left);
        var topEdge = indent + (rail ? RailTop + new string('\u2584', columns - 1) : new string(Rule, columns));
        var bottomEdge = indent + (rail ? RailBottom + new string('\u2580', columns - 1) : new string(Rule, columns));
        if (!string.Equals(state.Rows[0].Text.TrimEnd(' '), topEdge, StringComparison.Ordinal)
            || !string.Equals(bottom, bottomEdge, StringComparison.Ordinal)) { return false; }

        var width = columns - 2;
        if (words.Length <= width || (words.Length - 1) / width + 1 != state.Lines) { return false; }
        for (var line = 0; line < state.Lines; line++)
        {
            var start = line * width;
            var part = words.Substring(start, Math.Min(width, words.Length - start));
            // Content trims each row; use raw rows so a wrap cannot erase an internal space.
            if (line < state.Lines - 1 && part.EndsWith(' ')) { return false; }
            var prefix = rail ? $"{Rail} " : line == 0 ? $"{Glyph} " : "  ";
            var expected = (indent + prefix + part).TrimEnd(' ');
            if (!string.Equals(expected, state.Rows[line + 1].Text.TrimEnd(' '), StringComparison.Ordinal)) { return false; }
        }
        return true;
    }
}
