using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

/// <summary>
/// A GitHub Actions expression (the text inside <c>${{ }}</c>), parsed only as far as RunnerRouting needs: <c>||</c>,
/// <c>&amp;&amp;</c>, <c>!</c>, comparisons, literals, context paths and function calls. Parentheses leave no node,
/// and ToString prints one canonical form, so two spellings that differ only in spacing or grouping compare equal.
/// Anything else throws <see cref="FormatException"/>, which the caller reads as "can't tell", never as safe.
/// https://docs.github.com/en/actions/reference/workflows-and-actions/expressions
/// </summary>
abstract partial record Expr
{
    public sealed record Or(Expr[] Arms) : Expr { public override string ToString() => $"({string.Join(" || ", Arms.Select(a => a.ToString()))})"; }
    public sealed record And(Expr[] Arms) : Expr { public override string ToString() => $"({string.Join(" && ", Arms.Select(a => a.ToString()))})"; }
    public sealed record Not(Expr Operand) : Expr { public override string ToString() => $"!{Operand}"; }
    public sealed record Compare(Expr Left, string Op, Expr Right) : Expr { public override string ToString() => $"{Left} {Op} {Right}"; }
    public sealed record Call(string Name, Expr[] Args) : Expr { public override string ToString() => $"{Name}({string.Join(", ", Args.Select(a => a.ToString()))})"; }
    public sealed record Path(string Text) : Expr { public override string ToString() => Text; }
    public sealed record Literal(object? Value) : Expr
    {
        public override string ToString() => Value is string s ? $"'{s.Replace("'", "''", StringComparison.Ordinal)}'"
            : Value is bool b ? (b ? "true" : "false") : Value is null ? "null" : Convert.ToString(Value, CultureInfo.InvariantCulture)!;
    }

    public static Expr Parse(string text)
    {
        var parser = new Parser(Tokens(text));
        var expr = parser.Or();
        return parser.AtEnd ? expr : throw new FormatException($"unexpected {parser.Peek} in {text}");
    }

    // A string ('' escapes a quote), an operator or punctuation, or a word (names, numbers, true, a-b-c); nothing else.
    [GeneratedRegex(@"\G\s*(?:('(?:[^']|'')*')|(&&|\|\||==|!=|<=|>=|[()\[\],.!<>*])|([\w-]+))\s*")]
    private static partial Regex Token();

    static List<string> Tokens(string text)
    {
        var (tokens, at) = (new List<string>(), 0);
        // \G keeps the matches contiguous, so reaching the end means every character was read.
        for (var m = Token().Match(text); m.Success && m.Length > 0; m = m.NextMatch()) { tokens.Add(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value); at = m.Index + m.Length; }
        return text[at..].Trim().Length == 0 ? tokens : throw new FormatException($"can't read {text} at {at}");
    }

    sealed class Parser(List<string> tokens)
    {
        int at;
        public bool AtEnd => at == tokens.Count;
        public string Peek => AtEnd ? "the end" : tokens[at];
        bool Take(string token) { if (!AtEnd && tokens[at] == token) { at++; return true; } return false; }
        string Next() => AtEnd ? throw new FormatException("unexpected end") : tokens[at++];
        void Expect(string token) { if (!Take(token)) { throw new FormatException($"expected {token}, not {Peek}"); } }

        public Expr Or() => Chain("||", And, arms => new Or(arms));
        Expr And() => Chain("&&", Unary, arms => new And(arms));

        Expr Chain(string op, Func<Expr> next, Func<Expr[], Expr> make)
        {
            var arms = new List<Expr> { next() };
            while (Take(op)) { arms.Add(next()); }
            // Flatten (a && b) && c into one chain: grouping doesn't change what && or || returns.
            var flat = arms.SelectMany(a => op == "&&" && a is And and ? and.Arms : op == "||" && a is Or or ? or.Arms : [a]).ToArray();
            return flat.Length == 1 ? flat[0] : make(flat);
        }

        Expr Unary() => Take("!") ? new Not(Unary()) : Comparison();

        Expr Comparison()
        {
            var left = Primary();
            return !AtEnd && tokens[at] is "==" or "!=" or "<" or "<=" or ">" or ">=" ? new Compare(left, Next(), Primary()) : left;
        }

        Expr Primary()
        {
            var token = Next();
            if (token == "(") { var inner = Or(); Expect(")"); return inner; }
            if (token.StartsWith('\'')) { return new Literal(token[1..^1].Replace("''", "'", StringComparison.Ordinal)); }
            if (token is "true" or "false") { return new Literal(token == "true"); }
            if (token == "null") { return new Literal(null); }
            if (char.IsDigit(token[0]) || token[0] == '-') { return new Literal(double.Parse(token, CultureInfo.InvariantCulture)); }
            if (!char.IsLetter(token[0]) && token[0] != '_') { throw new FormatException($"unexpected {token}"); }
            if (Take("("))
            {
                var args = new List<Expr>();
                if (!Take(")")) { do { args.Add(Or()); } while (Take(",")); Expect(")"); }
                return new Call(token, [.. args]);
            }
            var path = new StringBuilder(token);
            while (true)
            {
                if (Take(".")) { path.Append('.').Append(Next()); }
                else if (Take("[")) { path.Append('[').Append(Or()).Append(']'); Expect("]"); }
                else { return new Path(path.ToString()); }
            }
        }
    }
}
