using System.IO.Compression;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_verified_standalone_hostw_bundle_replaces_the_sidecar_closure_requirement(bool partial, bool upperCase)
    {
        MakePackage();
        using (var zip = ZipFile.Open(Package, ZipArchiveMode.Update))
        {
            foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json", ".exe" })
            { zip.GetEntry("lib/app/" + HostwBundleTests.Host + suffix)!.Delete(); }
            using var output = zip.CreateEntry("lib/app/" + (upperCase ? HostwBundleTests.Host.ToUpperInvariant() : HostwBundleTests.Host) + ".exe").Open();
            output.Write(HostwBundleTests.Bundle(omit: partial ? "dependency.dll" : null));
        }
        if (partial) { Assert.Throws<InvalidDataException>(() => Prepare()); Assert.False(Directory.Exists(Slot)); }
        else { Assert.Equal(Version, Prepare().Version); Assert.True(Directory.Exists(Slot)); }
        Assert.False(File.Exists(Path.Combine(Root, "active-slot.json")));
        Assert.False(Directory.Exists(Path.Combine(Root, "current")));
    }
}
