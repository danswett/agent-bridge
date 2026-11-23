using System.Globalization;
using System.Text;

namespace AgentBridge.Quality;

/// <summary>
/// A GitHub Actions expression (the text inside <c>${{ }}</c>), parsed only as far as RunnerRouting needs: <c>||</c>,
/// <c>&amp;&amp;</c>, <c>!</c>, comparisons, literals, context paths and function calls. Parentheses leave no node,
/// and ToString prints one canonical form, so two spellings that differ only in spacing or grouping compare equal.
/// Anything else throws <see cref="FormatException"/>, which the caller reads as "can't tell", never as safe.
/// https://docs.github.com/en/actions/reference/workflows-and-actions/expressions
/// </summary>
abstract record Expr
{
    public sealed record Or(Expr[] Arms) : Expr { public override string ToString() => $"({string.Join(" || ", Arms.Select(a => a.ToString()))})"; }
    public sealed record And(Expr[] Arms) : Expr { public override string ToString() => $"({string.Join(" && ", Arms.Select(a => a.ToString()))})"; }
    public sealed record Not(Expr Operand) : Expr { public override string ToString() => $"!{Operand}"; }
    public sealed record Compare(Expr Left, string Op, Expr Right) : Expr { public override string ToString() => $"{Left} {Op} {Right}"; }
    public sealed record Call(string Name, Expr[] Args) : Expr { public override string ToString() => $"{Name}({string.Join(", ", Args.Select(a => a.ToString()))})"; }
    public sealed record Path(string Text) : Expr { public override string ToString() => Text; }
    public sealed record Literal(object? Value) : Expr
    {
        public override string ToString() => Value switch
        {
            string s => $"'{s.Replace("'", "''", StringComparison.Ordinal)}'",
            bool b => b ? "true" : "false",
            null => "null",
            _ => Convert.ToString(Value, CultureInfo.InvariantCulture)!,
        };
    }

    public static Expr Parse(string text)
    {
        var parser = new Parser(Tokens(text));
        var expr = parser.Or();
        return parser.AtEnd ? expr : throw new FormatException($"unexpected {parser.Peek} in {text}");
    }

    static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\'')
            {
                var s = new StringBuilder("'");
                for (i++; ; i++)
                {
                    if (i >= text.Length) { throw new FormatException("unterminated string"); }
                    if (text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { s.Append('\''); i++; continue; }
                    if (text[i] == '\'') { i++; break; }
                    s.Append(text[i]);
                }
                tokens.Add(s.ToString());
                continue;
            }
            var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
            if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=") { tokens.Add(two); i += 2; continue; }
            if ("()[],.!<>*".Contains(c)) { tokens.Add(c.ToString()); i++; continue; }
            if (char.IsLetterOrDigit(c) || c is '_' || c is '-')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' || (text[i] == '.' && char.IsDigit(text[start])))) { i++; }
                tokens.Add(text[start..i]);
                continue;
            }
            throw new FormatException($"unexpected character {c}");
        }
        return tokens;
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
            if (token.StartsWith('\'')) { return new Literal(token[1..]); }
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
