using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentBridge.App.Core;
using AgentBridge.Host.Delivery.Staging;
using Xunit;

// #984: the package Update.exe installs is the one checked against its release, through the
// installer's own open of it. Velopack's ApplyUpdatesAndRestart named the shared packages folder's
// file, which another profile could replace, from its own feed, between this app's check and the
// installer's open; now the installer is handed a checked copy in this profile's own folder.
public sealed class PackageHandoffTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abhand-" + Guid.NewGuid().ToString("N")[..8]);
    const string Source = "https://feed.b|False";
    const string Name = "AgentBridgeApp-0.2.0-full.nupkg";

    string Packages => Path.Combine(root, "install", "packages");
    string ProfileCopies => Path.Combine(root, "data", "work", "app-update");
    DownloadMark Mark => new(Path.Combine(root, "data", "app", "update-downloaded.txt"));

    public void Dispose()
    {
        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
    }

    static PackageDigest Digest(string bytes) =>
        new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bytes))).ToLowerInvariant());

    // The shared package, downloaded from feed B and marked as B's release.
    string Downloaded(string bytes = "release from feed B")
    {
        Directory.CreateDirectory(Packages);
        var package = Path.Combine(Packages, Name);
        File.WriteAllText(package, bytes);
        Mark.Accept(package, "0.2.0", Source, Digest(bytes));
        return package;
    }

    static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void A_package_swapped_in_the_shared_folder_after_it_was_verified_is_never_what_Update_exe_is_handed()
    {
        var shared = Downloaded();
        var handoff = new PackageHandoff(Mark, ProfileCopies);
        var copy = handoff.Prepare(shared, "0.2.0", Source);

        // Another profile writes its own feed's package of the same version over the shared one, as
        // it may until the installer opens it.
        File.WriteAllText(shared, "release from feed A");
        IReadOnlyList<string>? handed = null;
        using var held = handoff.Launch(copy, 4242, Path.Combine(root, "install"), Packages, ["--background", "--profile", "work"], args => handed = args);

        Assert.NotNull(handed);
        var package = After(handed, "--package");
        Assert.NotEqual(Path.GetFullPath(shared), Path.GetFullPath(package));
        Assert.StartsWith(Path.GetFullPath(ProfileCopies), Path.GetFullPath(package), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("release from feed B", ReadShared(package));
    }

    // The copy is checked again from the handle that keeps it, just before the installer starts: a
    // copy changed after it was made is never handed over, and nothing starts.
    [Fact]
    public void The_copy_is_checked_again_at_launch_and_one_changed_since_it_was_made_starts_nothing()
    {
        var handoff = new PackageHandoff(Mark, ProfileCopies);
        var copy = handoff.Prepare(Downloaded(), "0.2.0", Source);
        File.WriteAllText(copy.Package, "tampered");
        var started = false;

        Assert.Throws<PackageMismatchException>(() => handoff.Launch(copy, 4242, Path.Combine(root, "install"), Packages, [], _ => started = true));

        Assert.False(started);
    }

    // Held from the check until this process ends, the copy can't be changed under the installer.
    [Fact]
    public void The_copy_handed_over_is_held_unchangeable_until_the_app_lets_it_go()
    {
        var handoff = new PackageHandoff(Mark, ProfileCopies);
        var copy = handoff.Prepare(Downloaded(), "0.2.0", Source);
        using (var held = handoff.Launch(copy, 4242, Path.Combine(root, "install"), Packages, [], _ => { }))
        {
            // Windows keeps the file as held (the app runs only there); elsewhere sharing is advisory.
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.WriteAllText(copy.Package, "tampered"));
                Assert.Throws<IOException>(() => File.Delete(copy.Package));
            }
        }
        // A starter that fails lets it go, so the next attempt can make another.
        Assert.Throws<InvalidOperationException>(() => handoff.Launch(copy, 4242, Path.Combine(root, "install"), Packages, [], _ => throw new InvalidOperationException("Update.exe did not start")));
        File.WriteAllText(copy.Package, "replaced once let go");
    }

    // A shared package that is no longer the bytes marked is never copied, so never handed over.
    [Fact]
    public void A_shared_package_that_no_longer_matches_its_mark_is_never_copied()
    {
        var shared = Downloaded();
        File.WriteAllText(shared, "release from feed A");
        var handoff = new PackageHandoff(Mark, ProfileCopies);

        Assert.Throws<PackageMismatchException>(() => handoff.Prepare(shared, "0.2.0", Source));
        Assert.Throws<PackageMismatchException>(() => handoff.Prepare(Downloaded(), "0.2.0", "https://feed.a|False"));
        Assert.False(Directory.Exists(ProfileCopies) && Directory.EnumerateFiles(ProfileCopies).Any());
    }

    // Update.exe is started as Velopack's ApplyUpdatesAndRestart starts it (UpdateExe.Apply: apply,
    // --package, --waitPid, --rootDir, --packageDir, then the app's arguments after --), silent
    // (#1167), with --package naming the copy. --packageDir stays the installation's shared packages folder, where
    // Update.exe takes its install lock (packages\.velopack_lock), so one profile still can't install
    // over another's install.
    [Fact]
    public void Update_exe_is_handed_the_shared_packages_folder_so_its_install_lock_still_keeps_the_profiles_apart()
    {
        var handoff = new PackageHandoff(Mark, ProfileCopies);
        var copy = handoff.Prepare(Downloaded(), "0.2.0", Source);
        var install = Path.Combine(root, "install");
        IReadOnlyList<string>? handed = null;

        using var held = handoff.Launch(copy, 4242, install, Packages, ["--background", "--profile", "work"], args => handed = args);

        Assert.Equal(["--silent", "apply", "--package", copy.Package, "--waitPid", "4242", "--rootDir", install, "--packageDir", Packages, "--", "--background", "--profile", "work"], handed);
        Assert.NotEqual(Path.GetFullPath(Packages), Path.GetFullPath(Path.GetDirectoryName(copy.Package)!));
    }

    // #1167: Velopack's "Installing Update" window never shows; the app's own Updating screen says it.
    [Fact]
    public void Update_exe_is_started_silently_so_Velopacks_own_install_window_never_shows()
    {
        var handoff = new PackageHandoff(Mark, ProfileCopies);
        var copy = handoff.Prepare(Downloaded(), "0.2.0", Source);
        IReadOnlyList<string>? handed = null;

        using var held = handoff.Launch(copy, 4242, Path.Combine(root, "install"), Packages, ["--background"], args => handed = args);

        Assert.NotNull(handed);
        Assert.Equal("--silent", handed[0]);
        Assert.Equal("apply", handed[1]);
    }

    // Engineering's review of #1036 (6693104164, MEDIUM): the handle on the copy closes with the app,
    // before Update.exe opens it, so the folder it is in is private to this user, as the attachment
    // folder is (PrivateFolder): narrowed if it was made before with the default ACL, and the copy
    // inherits that (0600 elsewhere).
    [Fact]
    public void The_folder_the_copy_is_made_in_is_private_to_this_user_and_so_is_the_copy()
    {
        Directory.CreateDirectory(ProfileCopies);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(ProfileCopies, (UnixFileMode)0b111_101_101); }
        Assert.False(PrivateFolder.IsPrivate(ProfileCopies));

        var copy = new PackageHandoff(Mark, ProfileCopies).Prepare(Downloaded(), "0.2.0", Source);

        Assert.True(PrivateFolder.IsPrivate(ProfileCopies));
        if (OperatingSystem.IsWindows()) { AssertOnlyMe(ProfileCopies, copy.Package); }
        else { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copy.Package)); }
    }

    [SupportedOSPlatform("windows")]
    static void AssertOnlyMe(string folder, string file)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "inherits access from the folder above");
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.Equal(me, r.IdentityReference));
        Assert.Contains(rules, r => r.FileSystemRights.HasFlag(FileSystemRights.FullControl)
            && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        var fileRules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(fileRules);
        Assert.All(fileRules, r => Assert.Equal(me, r.IdentityReference));
    }

    // A copy an earlier hand-over left (installed since, or refused) is not kept: a release's size.
    [Fact]
    public void A_copy_left_by_an_earlier_hand_over_is_cleared()
    {
        var handoff = new PackageHandoff(Mark, ProfileCopies);
        var copy = handoff.Prepare(Downloaded(), "0.2.0", Source);
        Assert.True(File.Exists(copy.Package));

        handoff.Clear();

        Assert.False(File.Exists(copy.Package));
    }

    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
