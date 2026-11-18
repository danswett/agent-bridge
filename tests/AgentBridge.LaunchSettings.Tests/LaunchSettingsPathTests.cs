using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.LaunchSettings.Tests;
using AgentBridge.Local;
using Xunit;

namespace AgentBridge.LaunchSettings.Tests
{
    public sealed class LaunchSettingsPathTests
    {
        [WindowsPathTheory]
        [InlineData(@"\/h.invalid\s")]
        [InlineData(@"/\h.invalid\s")]
        [InlineData(@"\/?\C:\x")]
        [InlineData(@"/\?\C:\x")]
        [InlineData(@"\/.\C:\x")]
        [InlineData(@"/\.\C:\x")]
        [InlineData(@" \/?\UNC\h.invalid\s ")]
        [InlineData(@" /\?\UNC\h.invalid\s ")]
        public void Mixed_separator_UNC_and_device_paths_are_refused_before_candidate_IO(string requested)
        {
            var probe = Probe.Current = new();
            var before = probe.Config.ToArray();
            var baseline = Host.LaunchSettings.Handle("profile", new(), _ => { }).Baseline;
            var error = Record.Exception(() => Host.LaunchSettings.Handle("profile", new(baseline, [requested], true), _ => probe.Logs++));
            Assert.Equal(before, probe.Config);
            Assert.Equal(0, probe.Saves);
            Assert.Equal(0, probe.Logs);
            Assert.Empty(probe.Accesses);
            Assert.IsType<ArgumentException>(error);
        }

        [Fact]
        public void An_ordinary_absolute_path_reaches_the_candidate_filesystem_trap()
        {
            var probe = Probe.Current = new();
            var requested = Path.GetFullPath("workspace");
            var baseline = Host.LaunchSettings.Handle("profile", new(), _ => { }).Baseline;
            Assert.Throws<IOException>(() => Host.LaunchSettings.Handle("profile", new(baseline, [requested], true), _ => { }));
            Assert.Equal(requested, Assert.Single(probe.Accesses));
            Assert.Equal(0, probe.Saves);
        }
    }

    public sealed class WindowsPathTheoryAttribute : TheoryAttribute
    {
        public WindowsPathTheoryAttribute() { if (!OperatingSystem.IsWindows()) { Skip = "Windows mixed-separator path normalization"; } }
    }

    internal sealed class Probe
    {
        public static Probe Current = null!;
        public byte[] Config = Encoding.UTF8.GetBytes("""{"version":1,"retained":7}""");
        public readonly List<string> Accesses = [];
        public int Saves, Logs;
        public static T Access<T>(string path)
        {
            Current.Accesses.Add(path);
            throw new IOException("Unexpected candidate filesystem access: " + path);
        }
    }
}

// Only configuration reads are in-memory; every candidate filesystem boundary fails closed.
namespace AgentBridge.Host
{
    internal static class File
    {
        public static byte[] ReadAllBytes(string path) => path == Path.Combine("profile", HostSettings.FileName)
            ? Probe.Current.Config.ToArray() : Probe.Access<byte[]>(path);
    }
    internal sealed class HostSettings
    {
        public const string FileName = "config.json";
        public static readonly JsonDocumentOptions Options = new();
        public List<string> LaunchProblems { get; } = [];
        public HostSettings Launch => this;
        public List<Delivery.Launch.WorkspaceConfig> Workspaces { get; } = [];
        public static (HostSettings, string?) Parse(string text, string path) => (new(), null);
    }
}

namespace AgentBridge.Host.Migration
{
    internal static class Registrations
    {
        public static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);
        public static string? FirstLink(string path) => Probe.Access<string?>(path);
    }
    internal static class MigrationRecord
    {
        public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    }
    internal static class DurableFile
    {
        public static void Replace(string path, byte[] bytes, string? expected)
        { Probe.Current.Saves++; Probe.Current.Config = bytes; }
    }
}

namespace AgentBridge.Host.Delivery.Launch
{
    public sealed record WorkspaceConfig(string Path);
    internal static class Directory
    {
        public static bool Exists(string path) => Probe.Access<bool>(path);
    }
    internal sealed class DirectoryInfo(string path)
    {
        public string? LinkTarget => Probe.Access<string?>(path);
    }
}

namespace AgentBridge.Protocol
{
    internal static class LaunchProtocol { public const int MaxWorkspaces = 100; }
}
