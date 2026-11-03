using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Quality;

/// <summary>
/// The catalog's path globs, matched against repository-relative paths with forward slashes.
/// <c>*</c> and <c>?</c> stay within one folder and <c>**</c> crosses folders, so <c>*.md</c> is
/// the root's Markdown only and <c>docs/*.md</c> is not redundant beside it.
/// </summary>
sealed class Glob
{
    readonly Regex regex;

    public Glob(string pattern, bool ignoreCase = false)
    {
        Pattern = pattern;
        regex = new Regex(ToRegex(RepoPath.Normalize(pattern)), RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
    }

    public string Pattern { get; }

    public bool IsMatch(string path) => regex.IsMatch(path);

    static string ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slashBefore = i == 0 || glob[i - 1] == '/';
                var slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                if (slashBefore && slashAfter)
                {
                    // "a/**/b" also matches "a/b".
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
                sb.Append("[^/]*");
            else if (c == '?')
                sb.Append("[^/]");
            else
                sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.Append('$').ToString();
    }

    public override string ToString() => Pattern;
}

static class RepoPath
{
    /// <summary>Forward slashes, no leading "./" and no trailing slash, as git prints paths.</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.TrimEnd('/');
    }

    /// <summary>Joins a project folder and a relative MSBuild path, folding "..", as a repo path.</summary>
    public static string Combine(string folder, string relative)
    {
        var parts = new List<string>();
        foreach (var part in (folder.Length == 0 ? relative : folder + "/" + relative).Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    public static string Folder(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }
}
