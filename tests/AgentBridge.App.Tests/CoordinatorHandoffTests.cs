using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.App.Core;
using Xunit;

public sealed class CoordinatorHandoffTests : IDisposable
{
    const string Supported = "fixture host launcher [coordinator-v1]";
    readonly string home = Path.Combine(Path.GetTempPath(), "ab-handoff-" + Guid.NewGuid().ToString("N"));
    string Root => Path.Combine(home, "install");
    string Package => Path.Combine(Root, "packages", "older.nupkg");
    string Helper => Path.Combine(Root, "current", "agent-bridge-hostw.exe");
    string Destination => Path.Combine(home, "private-handoff");

    public CoordinatorHandoffTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Package)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Helper)!);
        File.WriteAllBytes(Helper, HostwBundleTests.Bundle());
        File.WriteAllText(Package, "older target package");
    }

    UpdateCoordinatorRequest Request() => new(Root, Path.GetDirectoryName(Package)!, Package,
        HostwBundleTests.Digest(File.ReadAllBytes(Package)), "AgentBridgeApp", "0.1.1",
        ["--background", "--profile", "two words"], 11, 123);

    [Fact]
    public void The_current_helper_and_older_target_have_separate_verified_custody()
    {
        var metadataStreams = new List<string>();
        var request = Request();
        using var held = new CoordinatorHandoff(Destination, HostwBundleTests.Target, image =>
        {
            var file = Assert.IsType<FileStream>(image);
            Assert.False(file.CanWrite);
            Assert.Throws<IOException>(() => File.OpenWrite(file.Name).Dispose());
            metadataStreams.Add(file.Name);
            return Supported;
        }).Prepare(request, Helper);

        Assert.Equal([Helper, held.ImagePath], metadataStreams);
        Assert.StartsWith(Destination + Path.DirectorySeparatorChar, held.ImagePath, StringComparison.Ordinal);
        Assert.True(Directory.Exists(held.ExtractionDirectory));
        Assert.Equal(File.ReadAllBytes(Helper), File.ReadAllBytes(held.ImagePath));
        var info = JsonSerializer.Deserialize<UpdateCoordinatorHandoff>(File.ReadAllText(held.ManifestPath))!;
        Assert.Equal(request.Digest, info.Apply.Digest);
        Assert.NotEqual(request.Package, info.Apply.Package);
        Assert.Equal(request.RestartArguments, info.Apply.RestartArguments);
        Assert.Equal("older target package", File.ReadAllText(info.Apply.Package));
        Assert.Equal(PackageDigest.Sha256, info.ImageDigest.Algorithm);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Helper))), info.ImageDigest.Hex);
        foreach (var path in new[] { held.ImagePath, held.ManifestPath, info.Apply.Package })
            Assert.Throws<IOException>(() => { File.OpenWrite(path).Dispose(); });
    }

    [Fact]
    public void A_B2_era_helper_refuses_distinctly_before_any_stage_write()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            new CoordinatorHandoff(Destination, HostwBundleTests.Target, _ => "older host launcher").Prepare(Request(), Helper));
        Assert.Contains("coordinator-protocol-unavailable", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public void A_changed_cached_target_cannot_create_a_handoff()
    {
        var request = Request();
        File.WriteAllText(Package, "changed");
        Assert.Throws<PackageMismatchException>(() =>
            new CoordinatorHandoff(Destination, HostwBundleTests.Target, _ => Supported).Prepare(request, Helper));
        Assert.False(Directory.Exists(Destination));
    }

    [Theory]
    [InlineData("inside-install")]
    [InlineData("install-ancestor")]
    [InlineData("relative-folder")]
    [InlineData("outside-helper")]
    public void Unsafe_paths_are_refused_before_metadata_or_any_write(string damage)
    {
        var folder = damage switch
        {
            "inside-install" => Path.Combine(Root, "handoff"),
            "install-ancestor" => home,
            "relative-folder" => "relative-handoff",
            _ => Destination
        };
        var helper = damage == "outside-helper" ? Path.Combine(home, "outside.exe") : Helper;
        if (helper != Helper) File.Copy(Helper, helper);
        var before = Directory.GetFileSystemEntries(home, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.Throws<InvalidDataException>(() => new CoordinatorHandoff(folder, HostwBundleTests.Target,
            _ => throw new InvalidOperationException("metadata must not run")).Prepare(Request(), helper));
        Assert.Equal(before, Directory.GetFileSystemEntries(home, "*", SearchOption.AllDirectories).Order().ToArray());
    }

    [Fact]
    public void A_marker_does_not_replace_the_shared_bundle_verifier_or_allow_target_helper_fallback()
    {
        File.WriteAllBytes(Package, HostwBundleTests.Bundle());
        File.WriteAllText(Helper, "not a bundle, even with compatible injected metadata");
        Assert.Throws<InvalidDataException>(() =>
            new CoordinatorHandoff(Destination, HostwBundleTests.Target, _ => Supported).Prepare(Request(), Helper));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public void A_non_directory_ancestor_is_rejected_before_staging()
    {
        var blocker = Path.Combine(home, "blocker");
        File.WriteAllText(blocker, "keep");
        Assert.ThrowsAny<IOException>(() => new CoordinatorHandoff(Path.Combine(blocker, "child"),
            HostwBundleTests.Target, _ => Supported).Prepare(Request(), Helper));
        Assert.Equal("keep", File.ReadAllText(blocker));
        Assert.False(Directory.Exists(Destination));
    }

    public void Dispose() => Directory.Delete(home, recursive: true);
}
