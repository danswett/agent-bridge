using System.IO.Compression;
using System.Security.Cryptography;
using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("ab-slots-").FullName;
    const string Version = "0.2.0";
    static readonly string[] Required = ["AgentBridge.exe", "AgentBridge.pri", "agent-bridge-host.exe",
        "agent-bridge-hostw.exe", "agent-bridge-injector.exe", "agent-bridge-host-hook.exe",
        "AgentBridge.dll", "AgentBridge.deps.json", "AgentBridge.runtimeconfig.json",
        "agent-bridge-host.dll", "agent-bridge-host.deps.json", "agent-bridge-host.runtimeconfig.json",
        "agent-bridge-hostw.dll", "agent-bridge-hostw.deps.json", "agent-bridge-hostw.runtimeconfig.json",
        "Assets/AgentBridge.ico", "Assets/AgentBridge.png", "Assets/Fonts/SlipgatesSans.ttc",
        "Assets/Fonts/SlipgatesSans-OFL.txt", "Assets/Fonts/SlipgatesMono.ttc", "Assets/Fonts/SlipgatesMono-OFL.txt",
        "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "dependency.dll"];
    string Root => Path.Combine(temp, "install");
    string Package => Path.Combine(temp, "release.nupkg");
    PackageDigest Digest => new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Package))));
    string Slot => Path.Combine(Root, "slots", $"{Version}-{Digest.Hex.ToLowerInvariant()}");

    void MakePackage(string? omit = null, string? extra = null, int attributes = 0, string manifestVersion = Version)
    {
        using var zip = ZipFile.Open(Package, ZipArchiveMode.Create);
        foreach (var name in Required.Append("Assets/nested/resource.dat").Where(n => n != omit))
        {
            using var writer = new StreamWriter(zip.CreateEntry("lib/app/" + name).Open());
            writer.Write(name.EndsWith(".deps.json", StringComparison.Ordinal)
                ? """{"runtimeTarget":{"name":"net10/win-x64"},"targets":{"net10/win-x64":{"app/1.0":{"runtime":{"dependency.dll":{}}}}}}"""
                : "verified " + name);
        }
        using (var writer = new StreamWriter(zip.CreateEntry("release.nuspec").Open()))
        {
            writer.Write($"<package><metadata><id>AgentBridgeApp</id><version>{manifestVersion}</version></metadata></package>");
        }
        if (extra is not null)
        {
            var entry = zip.CreateEntry(extra);
            entry.ExternalAttributes = attributes;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("untrusted");
        }
    }

    void ReadPayload()
    {
        using var zip = ZipFile.OpenRead(Package);
        SlotPreparation.Payload(zip, Version, static v => v == " v0.2.0+build " ? Version : v);
    }

    [Theory]
    [InlineData("lib/app/../escape")]
    [InlineData("lib/app/C:/escape")]
    [InlineData(@"lib/app/nested\escape")]
    [InlineData("/absolute")]
    [InlineData("lib/app/agentbridge.exe")]
    public void Unsafe_or_case_colliding_archive_paths_are_rejected(string entry)
    {
        MakePackage(extra: entry);
        Assert.Throws<InvalidDataException>(ReadPayload);
        Assert.False(Directory.Exists(Slot));
    }

    [Theory]
    [InlineData(0xA1FF0000)] // Unix symbolic link.
    [InlineData(0x00000400)] // Windows reparse point.
    public void Archive_links_are_not_extracted(uint attributes)
    {
        MakePackage(extra: "lib/app/link", attributes: unchecked((int)attributes));
        Assert.Throws<InvalidDataException>(ReadPayload);
    }

    [Theory]
    [MemberData(nameof(MissingFiles))]
    public void Incomplete_payloads_never_become_slots(string missing)
    {
        MakePackage(omit: missing);
        Assert.Throws<InvalidDataException>(ReadPayload);
        Assert.False(Directory.Exists(Slot));
    }
    public static IEnumerable<object[]> MissingFiles => Required.Select(n => new object[] { n });

    public void Dispose() => Directory.Delete(temp, recursive: true);
}
