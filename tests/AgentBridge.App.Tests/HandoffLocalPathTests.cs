using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentBridge.App.Core;
using AgentBridge.Host.Delivery.Staging;
using Xunit;

// The handoff layout is local (O158 follow-up, Quality's re-read of #4294): a loopback share of the install tree and a
// substituted drive both name the install root by a spelling that never string-matches it, so the final path of
// every folder is resolved through a retained handle and anything that is not a local drive path is refused.
public sealed class HandoffLocalPathTests : IDisposable
{
    const string Supported = "fixture host launcher [coordinator-v1]";
    readonly string home = Path.Combine(Path.GetTempPath(), "ab-local-" + Guid.NewGuid().ToString("N"));
    string Root => Path.Combine(home, "install");
    string Packages => Path.Combine(Root, "packages");
    string Package => Path.Combine(Packages, "older.nupkg");
    string Helper => Path.Combine(Root, "current", "agent-bridge-hostw.exe");

    public HandoffLocalPathTests()
    {
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Path.GetDirectoryName(Helper)!);
        File.WriteAllBytes(Helper, HostwBundleTests.Bundle());
        File.WriteAllText(Package, "older target");
    }

    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, recursive: true); }

    UpdateCoordinatorRequest Request(string root) => new(root, Path.Combine(root, "packages"), Path.Combine(root, "packages", "older.nupkg"),
        HostwBundleTests.Digest(File.ReadAllBytes(Package)), "AgentBridgeApp", "0.1.1", [], 11, 123);

    string[] InstallTree() => Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order().ToArray();

    // \\localhost\C$\... reaches the same tree through the machine's own administrative share. Reachable for an
    // administrator (the CI runner is one); where it is not, there is nothing to alias and the case has nothing to check.
    static string? Share(string path, string host)
    {
        var drive = path[0];
        var shared = $@"\\{host}\{drive}$\";
        return Directory.Exists(shared) ? shared + path[3..] : null;
    }

    [Theory]
    [InlineData(@"\\localhost\")]
    [InlineData(@"\\127.0.0.1\")]
    [InlineData(@"\\?\UNC\localhost\")]
    [InlineData(@"\\.\UNC\localhost\")]
    public void A_loopback_share_of_the_install_tree_cannot_receive_the_stage_folder(string prefix)
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = prefix.Contains("127.0.0.1", StringComparison.Ordinal) ? "127.0.0.1" : "localhost";
        if (Share(Root, host) is not { } share) return;
        var alias = prefix + share[(@"\\" + host + @"\").Length..] + @"\handoff";
        var before = InstallTree();

        var error = Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(alias, HostwBundleTests.Target, _ => Supported)
            .Prepare(Request(Root), Helper));

        Assert.Contains("handoff-path-refused", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, InstallTree());
        Assert.False(Directory.Exists(Path.Combine(Root, "handoff")));
    }

    [Fact]
    public void An_install_root_named_through_a_share_is_refused_even_with_a_local_stage_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Share(Root, "localhost") is not { } share) return;
        var before = InstallTree();

        Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(Path.Combine(Root, "handoff"), HostwBundleTests.Target, _ => Supported)
            .Prepare(Request(share), Path.Combine(share, "current", "agent-bridge-hostw.exe")));

        Assert.Equal(before, InstallTree());
    }

    [Fact]
    public void A_substituted_drive_for_the_install_root_cannot_receive_the_stage_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var drive = Substitute.Folder(Root);
        var before = InstallTree();

        // Z:\handoff is C:\...\install\handoff: only the handle's final path says so.
        Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(drive.Letter + @":\handoff", HostwBundleTests.Target, _ => Supported)
            .Prepare(Request(Root), Helper));

        Assert.Equal(before, InstallTree());
    }

    [Fact]
    public void A_substituted_drive_for_a_folder_outside_the_install_stages_under_that_folders_real_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(home, "outside");
        Directory.CreateDirectory(outside);
        using var drive = Substitute.Folder(outside);

        using var held = new CoordinatorHandoff(drive.Letter + @":\private", HostwBundleTests.Target, _ => Supported)
            .Prepare(Request(Root), Helper);

        // The real, plain local path: not the substituted letter, not a \\?\ or UNC spelling.
        Assert.StartsWith(Path.Combine(Path.GetFullPath(outside), "private") + Path.DirectorySeparatorChar, held.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(held.ImagePath.StartsWith(@"\\", StringComparison.Ordinal));
        Assert.True(File.Exists(held.ImagePath));
    }

    [Fact]
    public void A_path_segment_ending_in_a_space_or_dot_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var segment in new[] { "install ", "install." })
        {
            var folder = Path.Combine(home, segment, "handoff");
            var error = Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(folder, HostwBundleTests.Target, _ => Supported)
                .Prepare(Request(Root), Helper));
            Assert.Contains("ends in a dot or space", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_junction_in_the_stage_path_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var real = Path.Combine(home, "real");
        var link = Path.Combine(home, "link");
        Directory.CreateDirectory(real);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            if (mklink.ExitCode != 0) return;
        }

        try
        {
            var error = Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(Path.Combine(link, "handoff"), HostwBundleTests.Target, _ => Supported)
                .Prepare(Request(Root), Helper));

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(real));
        }
        finally { Directory.Delete(link); } // the junction alone, never its target (a recursive delete of home refuses it)
    }

    [Fact]
    public void The_stage_folders_and_files_are_private_to_this_user()
    {
        using var held = new CoordinatorHandoff(Path.Combine(home, "private-handoff"), HostwBundleTests.Target, _ => Supported)
            .Prepare(Request(Root), Helper);

        foreach (var folder in new[] { Path.GetDirectoryName(held.Directory)!, held.Directory, held.ExtractionDirectory })
            Assert.True(PrivateFolder.IsPrivate(folder), folder);
        if (OperatingSystem.IsWindows()) { AssertOnlyThisUserCanAccess(held); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void AssertOnlyThisUserCanAccess(CoordinatorHandoff.Copy held)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new FileSystemSecurity[]
        {
            new DirectoryInfo(held.Directory).GetAccessControl(),
            new FileInfo(held.ImagePath).GetAccessControl(),
            new FileInfo(held.ManifestPath).GetAccessControl(),
        };
        foreach (var one in security)
        {
            var rules = one.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            Assert.All(rules, rule => Assert.Equal(me, rule.IdentityReference));
        }
        Assert.True(security[0].AreAccessRulesProtected);
    }

    [Fact]
    public void A_failed_prepare_leaves_no_stage_folder()
    {
        var destination = Path.Combine(home, "private-handoff");
        var calls = 0;

        // The retained installed image proves v1; the staged copy no longer does, after the stage folder exists.
        var error = Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(destination, HostwBundleTests.Target,
            _ => ++calls == 1 ? Supported : "older host launcher").Prepare(Request(Root), Helper));

        Assert.Equal(2, calls);
        Assert.NotEmpty(error.Message);
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    // subst-style drive for a folder, removed on dispose (DefineDosDevice needs no elevation).
    sealed class Substitute : IDisposable
    {
        public char Letter { get; }
        Substitute(char letter) => Letter = letter;

        public static Substitute Folder(string folder)
        {
            for (var letter = 'Z'; letter >= 'H'; letter--)
            {
                if (Directory.Exists(letter + @":\")) continue;
                if (DefineDosDeviceW(0, letter + ":", folder)) return new Substitute(letter);
            }
            throw new InvalidOperationException("no free drive letter for the substituted-drive probe");
        }

        public void Dispose() => DefineDosDeviceW(2 | 4, Letter + ":", null);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DefineDosDeviceW(uint flags, string deviceName, string? targetPath);
    }
}
