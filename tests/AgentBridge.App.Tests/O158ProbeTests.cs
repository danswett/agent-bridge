using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using Xunit;

public sealed class O158ProbeTests
{
    [Theory]
    [InlineData(@"\\?\")]
    [InlineData(@"\\.\")]
    [InlineData("short")]
    public void A_Windows_install_alias_cannot_receive_the_staged_helper(string prefix)
    {
        if (!OperatingSystem.IsWindows()) return;
        var home = Path.Combine(Path.GetTempPath(), "ab-o158-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = Path.Combine(home, "install");
            var packages = Path.Combine(root, "packages");
            var helper = Path.Combine(root, "current", "agent-bridge-hostw.exe");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
            File.WriteAllBytes(helper, HostwBundleTests.Bundle());
            var package = Path.Combine(packages, "older.nupkg");
            File.WriteAllText(package, "older target");
            var request = new UpdateCoordinatorRequest(root, packages, package,
                HostwBundleTests.Digest(File.ReadAllBytes(package)), "AgentBridgeApp", "0.1.1", [], 11, 123);
            var alias = prefix + root + @"\handoff";
            if (prefix == "short")
            {
                var buffer = new char[32768];
                Assert.InRange(GetShortPathNameW(root, buffer, (uint)buffer.Length), 1u, (uint)buffer.Length - 1);
                alias = new string(buffer).TrimEnd('\0') + @"\handoff";
                Console.WriteLine("SHORT_ALIAS_AVAILABLE=" + !alias.Equals(root + @"\handoff", StringComparison.OrdinalIgnoreCase));
            }
            Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(alias, HostwBundleTests.Target,
                _ => "fixture host launcher [coordinator-v1]").Prepare(request, helper));
            Assert.False(Directory.Exists(Path.Combine(root, "handoff")));
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, recursive: true); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string path, [Out] char[] shortPath, uint length);
}
