namespace AgentBridge.App.Core;

// Which source (the feed, and the pre-release choice) the release last downloaded came from (#516).
// Velopack keeps the package in its folder across starts whatever the source selected since, so a
// start offers a package an earlier run downloaded only when this says it came from the source
// selected now. One that cannot be read vouches for nothing.
public sealed class DownloadMark(string file)
{
    public void Save(string version, string source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Text(version, source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public bool Vouches(string version, string source)
    {
        try { return File.Exists(file) && File.ReadAllText(file) == Text(version, source); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string Text(string version, string source) => $"{version}\n{source}";
}
