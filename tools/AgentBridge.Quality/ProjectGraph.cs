using System.Xml.Linq;

namespace AgentBridge.Quality;

/// <summary>A .csproj, named by its folder as the catalog's <c>project:</c> names it.</summary>
sealed record Project(string Folder, IReadOnlyList<string> References, IReadOnlyList<Glob> LinkedFiles, bool IsTestProject = false)
{
    public static Project Create(string folder, IEnumerable<string>? references = null, IEnumerable<string>? linkedFiles = null, bool isTestProject = false) =>
        new(RepoPath.Normalize(folder),
            (references ?? []).Select(RepoPath.Normalize).ToList(),
            (linkedFiles ?? []).Select(g => new Glob(g)).ToList(),
            isTestProject);
}

/// <summary>
/// The ProjectReference graph, read from the .csproj files every time so it can't drift from the
/// build the way a hand-kept list would.
/// </summary>
sealed class ProjectGraph
{
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", "TestResults", "out" };

    readonly Dictionary<string, Project> projects;
    readonly Dictionary<string, int> csprojCounts;
    readonly Dictionary<string, List<string>> referencedBy = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlySet<string>> reachers = new(StringComparer.Ordinal);

    public ProjectGraph(IEnumerable<Project> projects)
    {
        // Two .csproj files in one folder share its files, so the selector treats them as one
        // project; the catalog check refuses such a folder as a suite's project.
        var byFolder = projects.GroupBy(p => p.Folder, StringComparer.Ordinal).ToList();
        csprojCounts = byFolder.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        this.projects = byFolder.ToDictionary(
            g => g.Key,
            g => g.Count() == 1 ? g.First() : new Project(g.Key,
                g.SelectMany(p => p.References).Distinct(StringComparer.Ordinal).ToList(),
                g.SelectMany(p => p.LinkedFiles).ToList(),
                g.Any(p => p.IsTestProject)),
            StringComparer.Ordinal);
        foreach (var project in this.projects.Values)
            foreach (var reference in project.References)
            {
                if (!referencedBy.TryGetValue(reference, out var list))
                    referencedBy[reference] = list = [];
                list.Add(project.Folder);
            }
    }

    public IReadOnlyCollection<Project> Projects => projects.Values;

    public Project? this[string folder] => projects.GetValueOrDefault(RepoPath.Normalize(folder));

    /// <summary>The .csproj files directly in a folder.</summary>
    public int CsprojCount(string folder) => csprojCounts.GetValueOrDefault(RepoPath.Normalize(folder));

    public static ProjectGraph Load(string repoRoot)
    {
        var found = new List<Project>();
        foreach (var csproj in EnumerateProjects(repoRoot))
        {
            var relative = RepoPath.Normalize(Path.GetRelativePath(repoRoot, csproj));
            found.Add(Parse(RepoPath.Folder(relative), XDocument.Load(csproj)));
        }
        return new ProjectGraph(found);
    }

    public static Project Parse(string folder, XDocument csproj)
    {
        var references = new List<string>();
        var linked = new List<Glob>();
        var isTest = false;
        foreach (var element in csproj.Descendants())
        {
            var name = element.Name.LocalName;
            if (name == "IsTestProject" && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                isTest = true;
            var include = element.Attribute("Include")?.Value;
            if (include is null)
                continue;
            if (name == "PackageReference")
            {
                if (include == "Microsoft.NET.Test.Sdk")
                    isTest = true;
                continue;
            }
            foreach (var item in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Properties can't be evaluated without MSBuild, and none of ours need it yet.
                if (item.Contains("$(", StringComparison.Ordinal))
                    continue;
                if (name == "ProjectReference")
                    references.Add(RepoPath.Folder(RepoPath.Combine(folder, item)));
                else if (item.Replace('\\', '/').StartsWith("../", StringComparison.Ordinal))
                    // A file linked from outside the project's folder (a shared fixture, the protocol
                    // examples) belongs to this project too, or a change to it would reach nothing.
                    linked.Add(new Glob(RepoPath.Combine(folder, item)));
            }
        }
        return new Project(RepoPath.Normalize(folder), references, linked, isTest);
    }

    static IEnumerable<string> EnumerateProjects(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*.csproj"))
                yield return file;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!SkippedFolders.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
        }
    }

    /// <summary>
    /// The projects a file belongs to: the one whose folder is the longest prefix of its path, and
    /// any that link it in from outside their folder. A folder that projects still reference but
    /// whose .csproj is gone counts too: deleting a project, or its .csproj, must reach the tests
    /// that depend on it, not slip past them because the graph no longer lists it.
    /// </summary>
    public IReadOnlyList<Project> OwnersOf(string path)
    {
        var owners = new List<Project>();
        Project? byFolder = null;
        foreach (var folder in projects.Keys.Concat(referencedBy.Keys.Where(k => !projects.ContainsKey(k))))
        {
            if ((folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal))
                && (byFolder is null || folder.Length > byFolder.Folder.Length))
                byFolder = projects.GetValueOrDefault(folder) ?? Project.Create(folder);
        }
        if (byFolder is not null)
            owners.Add(byFolder);
        foreach (var project in projects.Values)
            if (project != byFolder && project.LinkedFiles.Any(g => g.IsMatch(path)))
                owners.Add(project);
        return owners;
    }

    /// <summary>The project and every project that reaches it through ProjectReferences.</summary>
    public IReadOnlySet<string> ReachersOf(string folder)
    {
        // A change touches many files in few projects, so each walk is done once per run.
        if (reachers.TryGetValue(folder, out var cached))
            return cached;
        var seen = new HashSet<string>(StringComparer.Ordinal) { folder };
        var queue = new Queue<string>([folder]);
        while (queue.Count > 0)
            foreach (var next in referencedBy.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (seen.Add(next))
                    queue.Enqueue(next);
        reachers[folder] = seen;
        return seen;
    }
}
