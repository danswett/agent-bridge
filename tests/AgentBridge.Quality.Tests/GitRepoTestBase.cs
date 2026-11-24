using AgentBridge.Quality;

// A throwaway git repository (branch "main") for the tests that need real history.
public abstract class GitRepoTestBase : IDisposable
{
    protected readonly string Repo = Path.Combine(Path.GetTempPath(), $"git-test-{Guid.NewGuid():N}");

    protected GitRepoTestBase()
    {
        Directory.CreateDirectory(Repo);
        Git("init", "-q", "-b", "main");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Repo, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        Directory.Delete(Repo, true);
    }

    protected string Git(params string[] args) => ChangedFiles.Git(Repo, ["-c", "user.name=git-test", "-c", "user.email=git-test@example.invalid", "-c", "commit.gpgsign=false", .. args]);

    protected void Commit(string subject, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Repo, path))!);
            File.WriteAllText(Path.Combine(Repo, path), text.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        Git("add", "-A");
        Git("commit", "-q", "-m", subject);
    }

    protected static string Lines(string prefix, int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"{prefix} {i}\n"));
}
