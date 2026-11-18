using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using AgentBridge.HostLauncher;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UpdaterImageTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "updater-pin-" + Guid.NewGuid().ToString("N"));
    static string Root => AppBrandTestsRoot();
    static string AppBrandTestsRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "AgentBridge.slnx"))) path = Path.GetDirectoryName(path)!;
        return path;
    }

    [Theory]
    [InlineData("win-x64", "update.exe")]
    [InlineData("win-arm64", "update_arm64.exe")]
    public void Trusted_packer_bytes_and_icon_reproduce_the_pin_and_signing_does_not_change_it(string runtime, string vendor)
    {
        var path = Prepare(vendor);
        using (UpdaterImage.Pin(path, runtime))
            Assert.Throws<IOException>(() => File.OpenWrite(path).Dispose());
        Assert.Throws<InvalidDataException>(() => UpdaterImage.Pin(path, runtime == "win-x64" ? "win-arm64" : "win-x64"));
        Assert.Throws<InvalidDataException>(() => UpdaterImage.Pin(path, "win-x86"));

        var bytes = File.ReadAllBytes(path);
        var optional = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60)) + 24;
        var security = optional + (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional)) == 0x10b ? 128 : 144);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(optional + 64), 12345);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(security), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(security + 4), 16);
        byte[] certificate = [16, 0, 0, 0, 0, 2, 2, 0, 1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(path, [.. bytes, .. certificate]);
        using (UpdaterImage.Pin(path, runtime)) { }
        File.WriteAllBytes(path, [.. bytes, .. certificate, 0]);
        Assert.Throws<InvalidDataException>(() => UpdaterImage.Pin(path, runtime));

        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        Assert.Equal("1.2.161", version);
        using (var pe = new PEReader(new MemoryStream(bytes)))
            bytes[pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text").PointerToRawData] ^= 1;
        File.WriteAllBytes(path, [.. bytes, .. certificate]);
        Assert.Equal(version, FileVersionInfo.GetVersionInfo(path).ProductVersion);
        Assert.Throws<InvalidDataException>(() => UpdaterImage.Pin(path, runtime));
        File.OpenWrite(path).Dispose();
    }

    string Prepare(string vendor)
    {
        Directory.CreateDirectory(folder);
        var packageRoot = typeof(UpdaterImageTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "VpkPackageRoot").Value!;
        var package = Path.Combine(packageRoot, "vpk", "1.2.161");
        using (var archive = File.OpenRead(Path.Combine(package, "vpk.1.2.161.nupkg")))
            Assert.Equal(UpdaterImage.SourceSha512, Convert.ToHexString(SHA512.HashData(archive)));
        var icon = Path.Combine(Root, "src", "AgentBridge.App", "Assets", "AgentBridge.ico");
        Assert.Equal(UpdaterImage.IconSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(icon))));
        var path = Path.Combine(folder, vendor);
        File.Copy(Path.Combine(package, "vendor", vendor), path);
        var tools = Path.Combine(package, "tools", "net10.0", "any");
        var context = new PackerContext(tools);
        try
        {
            var logging = context.LoadFromAssemblyPath(Path.Combine(tools, "Microsoft.Extensions.Logging.Abstractions.dll"));
            var logger = logging.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger")!.GetProperty("Instance")!.GetValue(null);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(tools, "Velopack.Packaging.Windows.dll"));
            var type = assembly.GetType("Velopack.Packaging.Windows.ResourceEdit")!;
            var editor = Activator.CreateInstance(type, path, logger);
            type.GetMethod("SetExeIcon")!.Invoke(editor, [icon]);
            type.GetMethod("Commit")!.Invoke(editor, null);
        }
        finally { context.Unload(); }
        return path;
    }

    sealed class PackerContext(string folder) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
