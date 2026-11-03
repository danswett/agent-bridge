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
    readonly Dictionary<string, List<string>> referencedBy = new(StringComparer.Ordinal);

    public ProjectGraph(IEnumerable<Project> projects)
    {
        this.projects = projects.ToDictionary(p => p.Folder, StringComparer.Ordinal);
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
    /// any that link it in from outside their folder.
    /// </summary>
    public IReadOnlyList<Project> OwnersOf(string path)
    {
        var owners = new List<Project>();
        Project? byFolder = null;
        foreach (var project in projects.Values)
        {
            if ((project.Folder.Length == 0 || path.StartsWith(project.Folder + "/", StringComparison.Ordinal))
                && (byFolder is null || project.Folder.Length > byFolder.Folder.Length))
                byFolder = project;
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
        var seen = new HashSet<string>(StringComparer.Ordinal) { folder };
        var queue = new Queue<string>([folder]);
        while (queue.Count > 0)
            foreach (var next in referencedBy.GetValueOrDefault(queue.Dequeue()) ?? [])
                if (seen.Add(next))
                    queue.Enqueue(next);
        return seen;
    }
}
