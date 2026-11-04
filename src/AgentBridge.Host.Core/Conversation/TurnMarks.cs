namespace AgentBridge.Host.Core.Conversation;

// The bytes a record that may start a turn carries anywhere in its line, and (Claude's tool
// results) those one that cannot does. An unescaped quoted value can only be a JSON token, never
// text inside a string, so a line without one cannot start a turn, wherever its writer put the
// property. Searched over the whole line: only looking at a line's first bytes dropped a prompt
// whose type came after a long property (found in review of #292). The live parser and history
// both use these, so a record they cannot read is a possible turn start to each alike.
internal sealed class TurnMarks
{
    readonly byte[][] _any;
    readonly byte[][] _none;

    TurnMarks(string[] any, string[] none)
    {
        _any = [.. any.Select(System.Text.Encoding.UTF8.GetBytes)];
        _none = [.. none.Select(System.Text.Encoding.UTF8.GetBytes)];
        Longest = _any.Concat(_none).Max(m => m.Length);
    }

    public static readonly TurnMarks Copilot = new(["\"user.message\""], []);
    public static readonly TurnMarks Claude = new(["\"user\""], ["\"tool_result\""]);
    public static readonly TurnMarks Codex = new(["\"user_message\"", "\"UserMessage\""], []);

    // The longest marker: a window this long less one either side of a boundary between two
    // pieces of a line finds a marker split across them.
    public int Longest { get; }

    // What one piece of a line carries; a line read in pieces ORs its pieces' findings.
    public (bool Any, bool None) Find(ReadOnlySpan<byte> bytes)
    {
        var any = false;
        foreach (var m in _any)
        {
            if (bytes.IndexOf(m) >= 0) { any = true; break; }
        }
        var none = false;
        foreach (var m in _none)
        {
            if (bytes.IndexOf(m) >= 0) { none = true; break; }
        }
        return (any, none);
    }

    public bool MayStart(ReadOnlySpan<byte> line) => Find(line) is (true, false);
}
