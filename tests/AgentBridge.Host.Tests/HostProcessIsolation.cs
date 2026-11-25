using System.Diagnostics;
using System.Security.Cryptography;

namespace AgentBridge.Host.Tests;

// #4520: a test that starts the real host or CLI as a process must give it a profile root of its own.
// LOCALAPPDATA is no isolation: the host finds its profile with Environment.GetFolderPath, which
// ignores that variable on Windows, so a child that was meant to be refused (or a guard that one day
// is not) wrote the machine's real config.json. AGENT_BRIDGE_PROFILE_ROOT is the one way, read in
// ProfileFolders.DefaultRoot; it is for tests and diagnostics, and it must be an absolute path.
static class HostProcessIsolation
{
    public const string RootVariable = "AGENT_BRIDGE_PROFILE_ROOT";

    public static string NewRoot(string prefix) => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    public static void Isolate(ProcessStartInfo start, string root) => start.Environment[RootVariable] = root;

    // Where the machine's real profiles are, worked out here and not by the product, so it is an
    // independent check. Only ever read.
    public static string RealRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-bridge");

    // What the real profiles' config.json files are right now: path, size, content hash and time of
    // each. Reads only; two equal snapshots mean nothing wrote, replaced or removed one.
    public static string[] RealConfigSnapshot()
    {
        var root = RealRoot();
        if (!Directory.Exists(root)) { return []; }
        List<string> files = [Path.Combine(root, "config.json")];
        files.AddRange(Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "config.json")));
        return [.. files.Order(StringComparer.OrdinalIgnoreCase).Select(Describe)];
    }

    static string Describe(string path)
    {
        if (!File.Exists(path)) { return path + "|absent"; }
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return $"{path}|{info.Length}|{Convert.ToHexString(SHA256.HashData(stream))}|{info.LastWriteTimeUtc.Ticks}";
    }
}
