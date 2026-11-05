using AgentBridge.Host;
using Xunit;

public sealed class MacDeliveryTests
{
    [Fact]
    public void Tmux_lookup_ignores_relative_PATH_entries_and_non_executable_files()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(Path.GetTempPath(), "bridge-tmux-lookup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "tmux");
            File.WriteAllText(path, "lookup fixture, never executed");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Null(MacDelivery.FindTmux(root));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Null(MacDelivery.FindTmux(Path.GetRelativePath(Environment.CurrentDirectory, root)));
            Assert.Equal(path, MacDelivery.FindTmux("." + Path.PathSeparator + root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
