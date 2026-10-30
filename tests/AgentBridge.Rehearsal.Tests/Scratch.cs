namespace AgentBridge.Rehearsal.Tests;

// A folder of its own for each test, removed afterwards.
internal sealed class Scratch : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rehearsal-tests", Guid.NewGuid().ToString("N"))).FullName;

    public string File(string relative, string content)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

// The repository's own files, found from the test's output folder.
internal static class Repo
{
    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (System.IO.File.Exists(Path.Combine(dir.FullName, "AgentBridge.slnx"))) { return dir.FullName; }
            }
            throw new DirectoryNotFoundException("AgentBridge.slnx not found above the test output");
        }
    }

    public static string Read(string relative) => System.IO.File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
}
