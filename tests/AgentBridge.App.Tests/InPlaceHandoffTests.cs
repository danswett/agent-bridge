using System.IO.Compression;
using System.Security.Cryptography;
using AgentBridge.App.Core;
using Xunit;

// #3343: the hand-over to the app's copy outside the installation; a fake Update.exe and held list, temp folders only.
public sealed class InPlaceHandoffTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abhandoff-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<IReadOnlyList<string>> runs = [];
    const string Nuspec = "<package><metadata><id>AgentBridgeApp</id><version>2.0.0</version><mainExe>AgentBridge.exe</mainExe></metadata></package>";
    string Current => Path.Combine(root, "install", "current");
    string Staged => Path.Combine(root, "helper-staged");
    string App => File.ReadAllText(Path.Combine(Current, "AgentBridge.dll"));
    public void Dispose() => Directory.Delete(root, recursive: true);
    public InPlaceHandoffTests()
    {
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "mscordaccore.dll"), "dac 10.0");
        File.WriteAllText(Path.Combine(Current, "AgentBridge.dll"), "app 1");
    }
    InPlaceHandoff Package(params string[] entries)
    {
        var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".nupkg");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create)) { foreach (var e in entries) { using var w = new StreamWriter(zip.CreateEntry(e.Split('=')[0]).Open()); w.Write(e.Split('=')[1]); } }
        return new(file, new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))), Path.Combine(root, "install"), Path.Combine(root, "install", "packages"), 1, 2, ["--profile", "p"]);
    }
    InPlaceHandoff Release() => Package("AgentBridgeApp.nuspec=" + Nuspec, "lib/app/AgentBridge.exe=exe 2", "lib/app/AgentBridge.dll=app 2",
        "lib/app/mscordaccore.dll=dac 10.0", "lib/app/Squirrel.exe=stub", "[Content_Types].xml=x");
    bool Install(InPlaceHandoff h, int exit) => h.Install(Staged, a => { runs.Add(a); return exit; }, () => [Path.Combine(Current, "mscordaccore.dll")]);

    [Fact]
    public void Only_a_refused_apply_while_a_file_is_held_installs_the_verified_package_in_place_without_a_restart()
    {
        Assert.False(Release().Install(Staged, _ => 0, () => throw new InvalidOperationException("asked")));
        Assert.False(Directory.Exists(Staged));
        Assert.Equal("app 1", App);
        Assert.True(Install(Release(), 1));
        Assert.Contains("--norestart", runs.Single());
        Assert.DoesNotContain("--wait", runs.Single());
        Assert.Equal("app 2", App);
        Assert.Equal(Nuspec, File.ReadAllText(Path.Combine(Current, "sq.version")));
        Assert.False(File.Exists(Path.Combine(Current, "Squirrel.exe")) || Directory.Exists(Staged));
    }

    [Fact]
    public void A_package_changed_since_the_app_verified_it_never_reaches_Update_exe_and_one_naming_a_place_outside_is_refused()
    {
        var h = Release();
        File.AppendAllText(h.Package, "x");
        Assert.Throws<PackageMismatchException>(() => Install(h, 1));
        Assert.Empty(runs);
        Assert.Throws<PackageMismatchException>(() => Install(Package("lib/app/../../escaped.dll=x", "lib/app/AgentBridge.dll=app 2"), 1));
        Assert.Equal("app 1", App);
        Assert.False(File.Exists(Path.Combine(root, "escaped.dll")));
    }

    [Fact]
    public void The_helper_runs_from_a_whole_copy_outside_the_installation_and_reads_its_hand_over_back()
    {
        var exe = InPlaceHandoff.CopyHelper(Current, Path.Combine(root, "profile", "app-update-helper"), "AgentBridge.exe");
        Assert.False(exe.StartsWith(Path.Combine(root, "install"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("dac 10.0", File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe)!, "mscordaccore.dll")));
        Assert.Equal(2, Directory.GetFiles(Current).Length);
        var h = Release();
        Assert.Equal(h.Arguments(), InPlaceHandoff.Parse(h.Arguments())!.Arguments());
        Assert.Null(InPlaceHandoff.Parse(["--background"]));
    }

    [Fact]
    public void The_in_place_install_holds_Velopack_s_package_lock_and_gives_up_while_another_install_has_it()
    {
        var h = Release();
        Directory.CreateDirectory(h.Packages);
        bool LockFree() { try { new FileStream(Path.Combine(h.Packages, ".velopack_lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None).Dispose(); return true; } catch (IOException) { return false; } }
        var freeWhileHeldWasAsked = true;
        Assert.True(h.Install(Staged, _ => 1, () => { freeWhileHeldWasAsked = LockFree(); return [Path.Combine(Current, "mscordaccore.dll")]; }));
        Assert.False(freeWhileHeldWasAsked);
        Assert.True(LockFree());
        File.WriteAllText(Path.Combine(Current, "AgentBridge.dll"), "app 1");
        using (new FileStream(Path.Combine(h.Packages, ".velopack_lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<InPlaceApplyException>(() => Install(Release(), 1));
        }
        Assert.Equal("app 1", App);
    }

    [Fact]
    public void A_verified_package_without_its_manifest_or_its_app_is_refused_before_current_is_touched()
    {
        Assert.Throws<PackageMismatchException>(() => Install(Package("[Content_Types].xml=x"), 1));
        Assert.Throws<PackageMismatchException>(() => Install(Package("AgentBridgeApp.nuspec=" + Nuspec, "lib/app/AgentBridge.dll=app 2"), 1));
        Assert.Equal("app 1", App);
        Assert.Equal("dac 10.0", File.ReadAllText(Path.Combine(Current, "mscordaccore.dll")));
    }

    [Fact]
    public void A_hand_over_run_inside_an_agent_session_installs_nothing_and_restarts_nothing()
    {
        var restarts = new List<IReadOnlyList<string>>();
        var logged = new List<string>();
        Assert.Equal(4, Release().Run(() => "inside a copilot process (pid 9)", Staged, a => { runs.Add(a); return 1; }, () => [Path.Combine(Current, "mscordaccore.dll")], restarts.Add, logged.Add));
        Assert.Empty(runs);
        Assert.Empty(restarts);
        Assert.Equal("app 1", App);
        Assert.Contains("copilot", Assert.Single(logged));
    }

    [Fact]
    public void A_hand_over_from_a_person_installs_and_then_starts_the_app_again()
    {
        var restarts = new List<IReadOnlyList<string>>();
        Assert.Equal(0, Release().Run(() => null, Staged, a => { runs.Add(a); return 1; }, () => [Path.Combine(Current, "mscordaccore.dll")], restarts.Add, _ => { }));
        Assert.Single(runs);
        Assert.Equal("app 2", App);
        Assert.Equal(["--profile", "p"], Assert.Single(restarts));
    }
}
