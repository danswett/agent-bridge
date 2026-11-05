using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentBridge.Quality;

/// <summary>
/// <c>diff-coverage</c> (quality/README.md §5): how many of the lines a change adds are covered,
/// read from the Cobertura report CI's product job already writes. It's advisory: a low number
/// warns and never fails. What it can't measure it says so (UNAVAILABLE, including C# outside
/// tests/ the report has no entry for), and a change with no executable line is NOT APPLICABLE,
/// so neither reads as 100%. Only a technical fault (a
/// malformed report or diff, a checkout not at --head) exits non-zero, through Program.Main.
/// </summary>
static class DiffCoverageCommand
{
    internal const string Usage = """
          diff-coverage --coverage FILE --base REF --head REF [--coverage FILE ...] [--repo DIR]
                        [--tests-outcome OUTCOME] [--scope TEXT] [--slug OWNER/REPO]
                        [--run-url URL] [--artifact NAME] [--out FILE]
              Advisory diff coverage: of the lines added from the merge base with --base to
              --head that a Cobertura report (ReportGenerator's, as in CI's coverage-<os>
              artifact) can execute, how many ran. The checkout must be at --head, where the
              report was measured. Several reports are unioned: a line is executable if any
              lists it, and covered if any ran it. UNAVAILABLE when a report is missing or holds
              no file in this checkout, --tests-outcome isn't success, or a C# file outside
              tests/ with added lines isn't in it (a warning naming them); NOT APPLICABLE when
              no added line is executable. Below 80% it warns; it never fails on the number.
        """;

    // quality/README.md §5's diff coverage target. Advisory: it only words the warning.
    internal const double Target = 80.0;

    static readonly HashSet<string> Valued = new(StringComparer.Ordinal) { "coverage", "base", "head", "repo", "tests-outcome", "scope", "slug", "run-url", "artifact", "out" };

    // GitHub's step outcomes, as steps.<id>.outcome gives them.
    static readonly string[] Outcomes = ["success", "failure", "cancelled", "skipped"];

    public static int Run(IReadOnlyList<string> args, TextWriter output)
    {
        var (options, reports) = ParseOptions(args);
        if (reports.Count == 0)
            throw new ArgumentException("diff-coverage needs --coverage FILE.");
        var baseRef = options.GetValueOrDefault("base") ?? throw new ArgumentException("diff-coverage needs --base REF.");
        var headRef = options.GetValueOrDefault("head") ?? throw new ArgumentException("diff-coverage needs --head REF.");
        var outcome = options.GetValueOrDefault("tests-outcome");
        if (outcome is not null && !Outcomes.Contains(outcome, StringComparer.Ordinal))
            throw new ArgumentException($"--tests-outcome {outcome} is not a step outcome: use one of {string.Join(", ", Outcomes)}.");
        var root = options.GetValueOrDefault("repo") ?? ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());

        // The report was measured on the checkout. Judging another commit's diff by it would
        // let an old head's numbers stand for a new one.
        var head = Commit(root, headRef);
        var checkout = Commit(root, "HEAD");
        if (!string.Equals(head, checkout, StringComparison.Ordinal))
            throw new InvalidOperationException($"The checkout is at {checkout}, not --head {headRef} ({head}): its coverage report can't speak for {headRef}.");
        var baseSha = Commit(root, baseRef);
        var mergeBase = ChangedFiles.Git(root, "merge-base", baseSha, head).Trim();
        var added = UnifiedDiff.AddedLines(ChangedFiles.Git(root, [.. UnifiedDiff.GitArgs, mergeBase, head]));

        var result = DiffCoverage.Evaluate(root, added, reports, outcome);
        var identity = new DiffCoverageIdentity(
            options.GetValueOrDefault("slug") ?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "(this repository)",
            head, baseRef, baseSha, mergeBase,
            options.GetValueOrDefault("artifact"), options.GetValueOrDefault("run-url"), outcome, options.GetValueOrDefault("scope"), reports);
        var markdown = DiffCoverage.Render(result, identity);
        if (options.TryGetValue("out", out var file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, markdown);
        }
        if (DiffCoverage.Annotation(result) is { } annotation)
            output.WriteLine(annotation);
        output.Write(markdown);
        return 0;
    }

    static string Commit(string root, string rev)
    {
        // A revision is never an option to git.
        if (rev.Length == 0 || rev.StartsWith('-'))
            throw new ArgumentException($"'{rev}' is not a revision.");
        return ChangedFiles.Git(root, "rev-parse", "--verify", rev + "^{commit}").Trim();
    }

    static (Dictionary<string, string> Options, List<string> Reports) ParseOptions(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var reports = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is null || !Valued.Contains(name))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"--{name} needs a value.");
            if (name == "coverage")
                reports.Add(args[++i]);
            else if (!options.TryAdd(name, args[++i]))
                throw new ArgumentException($"--{name} is given twice.");
        }
        return (options, reports);
    }
}

/// <summary>The lines a unified diff adds, by the path they're added at.</summary>
static class UnifiedDiff
{
    /// <summary>
    /// The diff to read: no context, so a hunk's new side is exactly its added lines; renames
    /// detected, so a moved file counts only the lines that changed; and every setting that could
    /// change the format or the content (external diff, textconv, prefixes, colour, relative)
    /// pinned, whatever the machine's git config says.
    /// </summary>
    internal static readonly string[] GitArgs = ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-relative", "--unified=0", "-M", "--src-prefix=a/", "--dst-prefix=b/"];

    static readonly Regex Hunk = new(@"^@@ -[0-9]+(?:,([0-9]+))? \+([0-9]+)(?:,([0-9]+))? @@", RegexOptions.CultureInvariant);

    public static SortedDictionary<string, SortedSet<int>> AddedLines(string diff)
    {
        var files = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        string? path = null;
        var oldLeft = 0;
        var newLeft = 0;
        var lines = diff.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Inside a hunk the counts say where it ends, so an added line that reads like a
            // header ("+++ x" adds "++ x") is never taken for one.
            if (oldLeft > 0 || newLeft > 0)
            {
                if (line.StartsWith('-') && oldLeft > 0)
                    oldLeft--;
                else if (line.StartsWith('+') && newLeft > 0)
                    newLeft--;
                else if (!line.StartsWith('\\'))
                    throw new FormatException($"Line {i + 1} of the diff doesn't belong to its hunk: '{Clip(line)}'.");
                continue;
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                path = null;
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                path = NewPath(line[4..], i + 1);
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var match = Hunk.Match(line);
                if (!match.Success)
                    throw new FormatException($"Line {i + 1} of the diff is not a hunk header: '{Clip(line)}'.");
                oldLeft = Count(match.Groups[1], i + 1);
                var start = Count(match.Groups[2], i + 1);
                newLeft = Count(match.Groups[3], i + 1);
                if (newLeft > 0 && path is null)
                    throw new FormatException($"Line {i + 1} of the diff adds lines to no file.");
                if (newLeft > 0 && start < 1)
                    throw new FormatException($"Line {i + 1} of the diff adds lines before line 1.");
                if (newLeft > 0)
                {
                    if (!files.TryGetValue(path!, out var set))
                        files[path!] = set = new();
                    for (var n = 0; n < newLeft; n++)
                        set.Add(start + n);
                }
            }
            // Everything else is a file's header (index, mode, rename, similarity, "---", binary).
        }
        if (oldLeft > 0 || newLeft > 0)
            throw new FormatException("The diff ends inside a hunk.");
        return files;
    }

    static int Count(Group group, int line)
    {
        if (!group.Success)
            return 1;
        if (!int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            throw new FormatException($"Line {line} of the diff has a count out of range: {group.Value}.");
        return n;
    }

    // "+++ b/path", "+++ /dev/null" for a deletion, or "+++ \"b/odd\\tname\"" for a name git quotes.
    // git ends the name with a tab when it holds a space.
    static string? NewPath(string raw, int line)
    {
        var name = raw.EndsWith('\t') ? raw[..^1] : raw;
        if (name == "/dev/null")
            return null;
        if (name.StartsWith('"'))
            name = Unquote(name, line);
        if (!name.StartsWith("b/", StringComparison.Ordinal) || name.Length == 2)
            throw new FormatException($"Line {line} of the diff names no b/ path: '{Clip(raw)}'.");
        return name[2..];
    }

    // git's C-style quoting: \\ \" \a \b \f \n \r \t \v and three octal digits per byte.
    internal static string Unquote(string quoted, int line)
    {
        if (quoted.Length < 2 || !quoted.EndsWith('"'))
            throw new FormatException($"Line {line} of the diff has an unterminated quoted path.");
        var bytes = new List<byte>();
        var text = new StringBuilder();
        void Flush()
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(text.ToString()));
            text.Clear();
        }
        for (var i = 1; i < quoted.Length - 1; i++)
        {
            var c = quoted[i];
            if (c != '\\')
            {
                text.Append(c);
                continue;
            }
            Flush();
            if (++i >= quoted.Length - 1)
                throw new FormatException($"Line {line} of the diff has a quoted path ending in a lone backslash.");
            var e = quoted[i];
            char? simple = e switch { '\\' => '\\', '"' => '"', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => null };
            if (simple is { } s)
                bytes.Add((byte)s);
            else if (i + 2 < quoted.Length - 1 && IsOctal(e) && IsOctal(quoted[i + 1]) && IsOctal(quoted[i + 2]) && e <= '3')
            {
                bytes.Add((byte)(((e - '0') << 6) | ((quoted[i + 1] - '0') << 3) | (quoted[i + 2] - '0')));
                i += 2;
            }
            else
                throw new FormatException($"Line {line} of the diff has an unknown escape in a quoted path: \\{e}.");
        }
        Flush();
        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
    }

    static bool IsOctal(char c) => c is >= '0' and <= '7';

    static string Clip(string line) => line.Length <= 80 ? line : line[..80] + "...";
}

/// <summary>
/// Line coverage by repository path, from Cobertura reports. Only each class's own &lt;lines&gt;
/// are read (its methods repeat them). A file can appear in several classes and packages (a
/// partial class, a file linked into two assemblies) and in several reports: a line is
/// executable if any of them lists it, and covered if any of them ran it.
/// </summary>
sealed class LineCoverage
{
    // Windows paths compare without case, as the file system does.
    readonly Dictionary<string, Dictionary<int, bool>> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Class entries whose file is outside the checkout (a package, generated code): ignored.</summary>
    public int Outside { get; private set; }

    public int FileCount => files.Count;

    public bool Measures(string path) => files.ContainsKey(path);

    /// <summary>Whether the line ran, or null when the report lists no code on it.</summary>
    public bool? Covered(string path, int line) =>
        files.TryGetValue(path, out var lines) && lines.TryGetValue(line, out var covered) ? covered : null;

    public void Read(string repoRoot, string reportPath)
    {
        // The DOCTYPE ReportGenerator writes names a DTD on the web: never fetched.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        XDocument document;
        try
        {
            using var stream = File.OpenRead(reportPath);
            using var reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new FormatException($"{reportPath} is not well-formed XML: {e.Message}", e);
        }
        Add(repoRoot, document, reportPath);
    }

    internal void Add(string repoRoot, XDocument document, string reportName)
    {
        var root = document.Root;
        if (root is null || root.Name != "coverage")
            throw new FormatException($"{reportName} is not a Cobertura report: its root is <{root?.Name}>, not <coverage>.");
        var sources = root.Elements("sources").Elements("source").Select(s => s.Value.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var cls in root.Elements("packages").Elements("package").Elements("classes").Elements("class"))
        {
            var filename = (string?)cls.Attribute("filename");
            if (string.IsNullOrWhiteSpace(filename))
                throw new FormatException($"{reportName}{At(cls)}: a <class> has no filename.");
            var path = RepoRelative(repoRoot, filename, sources);
            if (path is null)
            {
                Outside++;
                continue;
            }
            if (!files.TryGetValue(path, out var lines))
                files[path] = lines = new();
            foreach (var line in cls.Elements("lines").Elements("line"))
            {
                var number = Number(line, "number", reportName);
                var hits = Number(line, "hits", reportName);
                if (number < 1)
                    throw new FormatException($"{reportName}{At(line)}: line number {number} is before line 1.");
                lines[(int)number] = lines.GetValueOrDefault((int)number) || hits > 0;
            }
        }
    }

    static long Number(XElement line, string attribute, string reportName)
    {
        var value = (string?)line.Attribute(attribute);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || (attribute == "number" && n > int.MaxValue))
            throw new FormatException($"{reportName}{At(line)}: <line {attribute}=\"{value}\"> is not a count.");
        return n;
    }

    static string At(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? $", line {info.LineNumber}" : "";

    static readonly Regex DriveRooted = new(@"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The report's filename as a path in the checkout, or null when it resolves outside it.
    /// Absolute, as ReportGenerator writes them; otherwise against each &lt;source&gt;, or the
    /// checkout when there's none. Nothing is read from the path: it's only compared.
    /// </summary>
    internal static string? RepoRelative(string repoRoot, string filename, IReadOnlyList<string> sources)
    {
        var fullRoot = Path.GetFullPath(repoRoot);
        // Another system's absolute path (D:\a\... read on Linux) would otherwise join the checkout.
        if (!Path.IsPathFullyQualified(filename) && (Path.IsPathRooted(filename) || DriveRooted.IsMatch(filename)))
            return null;
        IEnumerable<string> candidates = Path.IsPathFullyQualified(filename) ? [filename]
            : sources.Count > 0 ? sources.Select(s => Path.Combine(Path.GetFullPath(s, fullRoot), filename))
            : [Path.Combine(fullRoot, filename)];
        foreach (var candidate in candidates)
        {
            var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate, fullRoot));
            if (Path.IsPathRooted(relative) || relative == "." || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("../", StringComparison.Ordinal))
                continue;
            return RepoPath.Normalize(relative);
        }
        return null;
    }
}

enum DiffCoverageStatus { Measured, NotApplicable, Unavailable }

/// <param name="Executable">Added lines the report lists code on.</param>
/// <param name="Uncovered">Of those, the ones that never ran.</param>
/// <param name="Measured">False when the report has no entry for the file at all.</param>
sealed record DiffCoverageFile(string Path, int Added, int Executable, int Covered, IReadOnlyList<int> Uncovered, bool Measured);

/// <param name="Unknown">
/// Production C# files with added lines that the report doesn't measure. Their lines may or may
/// not be executable, so they make the whole result UNAVAILABLE, never NOT APPLICABLE or a
/// percentage of the rest.
/// </param>
sealed record DiffCoverageResult(DiffCoverageStatus Status, string? Reason, IReadOnlyList<DiffCoverageFile> Files, int Executable, int Covered, int Outside, IReadOnlyList<string> Unknown)
{
    public int Added => Files.Sum(f => f.Added);
    public double Percent => Executable == 0 ? 0 : 100.0 * Covered / Executable;
    public bool BelowTarget => Status == DiffCoverageStatus.Measured && Percent < DiffCoverageCommand.Target;
}

/// <summary>What a result was measured on, so it's never read as another commit's or run's.</summary>
sealed record DiffCoverageIdentity(string Slug, string Head, string BaseRef, string Base, string MergeBase,
    string? Artifact, string? RunUrl, string? TestsOutcome, string? Scope, IReadOnlyList<string> Reports);

static class DiffCoverage
{
    /// <summary>
    /// Every added line against the reports. A missing report, or reports with no file in this
    /// checkout, or tests that didn't all run to success, leave the result UNAVAILABLE rather
    /// than a percentage of whatever was left.
    /// </summary>
    public static DiffCoverageResult Evaluate(string repoRoot, IReadOnlyDictionary<string, SortedSet<int>> added, IReadOnlyList<string> reports, string? testsOutcome)
    {
        var unmeasured = added.Select(f => new DiffCoverageFile(f.Key, f.Value.Count, 0, 0, [], false)).ToList();
        if (testsOutcome is not null && testsOutcome != "success")
            return new(DiffCoverageStatus.Unavailable, $"dotnet test ended {testsOutcome}, so the report holds only what ran before that.", unmeasured, 0, 0, 0, []);
        var missing = reports.Where(r => !File.Exists(r)).ToList();
        if (missing.Count > 0)
            return new(DiffCoverageStatus.Unavailable, $"No coverage report at {string.Join(", ", missing)}.", unmeasured, 0, 0, 0, []);
        var coverage = new LineCoverage();
        foreach (var report in reports)
            coverage.Read(repoRoot, report);
        return Evaluate(added, coverage);
    }

    internal static DiffCoverageResult Evaluate(IReadOnlyDictionary<string, SortedSet<int>> added, LineCoverage coverage)
    {
        var files = new List<DiffCoverageFile>();
        foreach (var (path, lines) in added)
        {
            if (!coverage.Measures(path))
            {
                files.Add(new(path, lines.Count, 0, 0, [], false));
                continue;
            }
            var executable = 0;
            var uncovered = new List<int>();
            foreach (var line in lines)
            {
                if (coverage.Covered(path, line) is not { } ran)
                    continue;
                executable++;
                if (!ran)
                    uncovered.Add(line);
            }
            files.Add(new(path, lines.Count, executable, executable - uncovered.Count, uncovered, true));
        }
        var total = files.Sum(f => f.Executable);
        var covered = files.Sum(f => f.Covered);
        // A report that measured nothing here (an empty run, another checkout's paths) says
        // nothing about these lines, which would otherwise all read as not executable.
        if (coverage.FileCount == 0)
            return new(DiffCoverageStatus.Unavailable, $"The report lists no file in this checkout ({coverage.Outside} class entries outside it).", files, 0, 0, coverage.Outside, []);
        // A file the report has no entry for isn't a file with no executable line: its assembly
        // may be one no selected test loads (the WinUI app, a new project). So unless it's
        // something no test measures anyway, the change's coverage is unknown.
        var unknown = files.Where(f => !f.Measured && MayBeMeasured(f.Path)).Select(f => f.Path).ToList();
        if (unknown.Count > 0)
            return new(DiffCoverageStatus.Unavailable, $"The report doesn't measure {unknown.Count} C# file(s) outside tests/ that this change adds lines to, so its coverage is unknown.", files, total, covered, coverage.Outside, unknown);
        return new(total == 0 ? DiffCoverageStatus.NotApplicable : DiffCoverageStatus.Measured, null, files, total, covered, coverage.Outside, []);
    }

    /// <summary>
    /// Whether a file is .NET source a report could measure: C# outside tests/, where the test
    /// projects and their helpers live (build/coverage.settings.xml leaves the test assemblies
    /// out). Anything else (scripts, docs, the PWA, tests) a missing entry can't make unknown.
    /// </summary>
    internal static bool MayBeMeasured(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);

    const int MaxRows = 30;
    const int MaxRanges = 12;

    public static string Render(DiffCoverageResult result, DiffCoverageIdentity identity)
    {
        var sb = new StringBuilder();
        var title = identity.Artifact is null ? "" : $": {identity.Artifact}";
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Diff coverage, advisory{title}");
        sb.AppendLine();
        var changed = $"{result.Added} added line(s) in {result.Files.Count} file(s)";
        var subset = result.Executable == 0 ? ""
            : $" In the files it does measure only, a subset and not this change's coverage, {Percent(result.Percent)} of the {result.Executable} executable added line(s) ran ({result.Covered} covered).";
        sb.AppendLine(result.Status switch
        {
            DiffCoverageStatus.Unavailable when result.Unknown.Count > 0 => $"**UNAVAILABLE.** {result.Reason} Not measured: {Names(result.Unknown, Code)}.{subset}",
            DiffCoverageStatus.Unavailable => $"**UNAVAILABLE.** {result.Reason} The {changed} are counted neither covered nor uncovered.",
            DiffCoverageStatus.NotApplicable => $"**NOT APPLICABLE.** None of the {changed} is code the report can run (comments and declarations in the files it measures, or files that aren't C# outside tests/), so there's nothing to cover.",
            _ => $"**{Percent(result.Percent)}** of the {result.Executable} executable added line(s) ran ({result.Covered} covered)"
                + (result.BelowTarget ? $", below the {Percent(DiffCoverageCommand.Target)} target." : $", at or above the {Percent(DiffCoverageCommand.Target)} target."),
        });
        sb.AppendLine();
        sb.AppendLine("Advisory only: nothing fails on it, and no floor applies (quality/README.md §5). Line coverage; branches aren't counted.");

        var measured = result.Files.Where(f => f.Measured && f.Executable > 0).ToList();
        if ((result.Status == DiffCoverageStatus.Measured || result.Unknown.Count > 0) && measured.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| File | Covered | Executable added | Not run |");
            sb.AppendLine("| --- | ---: | ---: | --- |");
            foreach (var f in measured.OrderBy(f => f.Covered - f.Executable).ThenBy(f => f.Path, StringComparer.Ordinal).Take(MaxRows))
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {Code(f.Path)} | {f.Covered} | {f.Executable} | {Ranges(f.Uncovered)} |");
            if (measured.Count > MaxRows)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| (+{measured.Count - MaxRows} more) | | | |");
        }

        var notMeasured = result.Files.Where(f => !f.Measured && !result.Unknown.Contains(f.Path)).ToList();
        if ((result.Status != DiffCoverageStatus.Unavailable || result.Unknown.Count > 0) && notMeasured.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Not counted: {notMeasured.Sum(f => f.Added)} added line(s) in {notMeasured.Count} file(s) that aren't C# outside tests/ (scripts, docs, tests) and that the report doesn't measure: "
                + Names(notMeasured.Select(f => f.Path).ToList(), Code) + ".");
        }

        sb.AppendLine();
        var run = identity.RunUrl is null ? "" : $" of [this run]({identity.RunUrl})";
        var from = identity.Artifact is null ? string.Join(", ", identity.Reports.Select(Code)) : $"{Code(identity.Artifact)}{run}";
        sb.AppendLine($"Measured on {Code(identity.Slug)} at {Code(identity.Head)}: the diff from {Code(identity.BaseRef)} ({Code(Short(identity.Base))}, merge base {Code(Short(identity.MergeBase))}), against the coverage in {from}"
            + (identity.TestsOutcome is null ? "" : $", whose tests ended {identity.TestsOutcome}")
            + (identity.Scope is null ? "." : $", from {identity.Scope}."));
        if (result.Outside > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"{result.Outside} class entries outside the checkout were ignored.");
        return sb.ToString();
    }

    /// <summary>A workflow command for the run's annotations: a warning below the target or for unmeasured C#, a notice when otherwise unavailable.</summary>
    public static string? Annotation(DiffCoverageResult result) => result.Status switch
    {
        DiffCoverageStatus.Unavailable when result.Unknown.Count > 0 => $"::warning title=Diff coverage (advisory)::{Escape($"UNAVAILABLE: {result.Reason} Not measured: {Names(result.Unknown, p => p)}. Advisory: nothing fails on it.")}",
        DiffCoverageStatus.Unavailable => $"::notice title=Diff coverage (advisory)::{Escape($"UNAVAILABLE: {result.Reason}")}",
        DiffCoverageStatus.Measured when result.BelowTarget => $"::warning title=Diff coverage (advisory)::{Escape($"{Percent(result.Percent)} of {result.Executable} executable added lines ran, below the {Percent(DiffCoverageCommand.Target)} target. Advisory: nothing fails on it.")}",
        _ => null,
    };

    // The first ten paths, and how many more.
    static string Names(IReadOnlyList<string> paths, Func<string, string> format) =>
        string.Join(", ", paths.Take(10).Select(format)) + (paths.Count > 10 ? $" (+{paths.Count - 10} more)" : "");

    // GitHub's escaping for a workflow command's message.
    static string Escape(string message) => message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    static string Percent(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

    static string Code(string text) => "`" + text.Replace('`', '\'').Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ') + "`";

    /// <summary>"3, 7-9, 12", at most a dozen runs of lines.</summary>
    internal static string Ranges(IReadOnlyList<int> lines)
    {
        var runs = new List<string>();
        for (var i = 0; i < lines.Count;)
        {
            var j = i;
            while (j + 1 < lines.Count && lines[j + 1] == lines[j] + 1)
                j++;
            runs.Add(i == j ? lines[i].ToString(CultureInfo.InvariantCulture) : $"{lines[i].ToString(CultureInfo.InvariantCulture)}-{lines[j].ToString(CultureInfo.InvariantCulture)}");
            i = j + 1;
        }
        return runs.Count <= MaxRanges ? string.Join(", ", runs) : string.Join(", ", runs.Take(MaxRanges)) + $" (+{runs.Count - MaxRanges} more)";
    }
}
