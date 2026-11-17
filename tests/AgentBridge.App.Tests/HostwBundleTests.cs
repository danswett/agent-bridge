using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.App.Core;
using Xunit;

public sealed class HostwBundleTests
{
    internal const string Target = "net10/win-x64";
    internal const string Host = "agent-bridge-hostw";
    const string Deps = """{"runtimeTarget":{"name":"net10/win-x64"},"targets":{"net10/win-x64":{"hostw/1":{"runtime":{"agent-bridge-hostw.dll":{},"System.Private.CoreLib.dll":{},"dependency.dll":{}},"native":{"coreclr.dll":{},"clrjit.dll":{}}}}}}""";
    const string Config = """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}""";
    internal static PackageDigest Digest(byte[] bytes) => new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));

    // Synthetic PE/bundle bytes following .NET 10's published v6 manifest format; never executable.
    internal static byte[] Bundle(string? omit = null, bool compressed = false, string? extra = null)
    {
        var files = new Dictionary<string, (byte Type, byte[] Bytes)>
        {
            [Host + ".dll"] = (1, [1, 2, 3]), ["System.Private.CoreLib.dll"] = (1, [4, 5, 6]),
            ["dependency.dll"] = (1, Enumerable.Repeat((byte)7, 128).ToArray()),
            ["coreclr.dll"] = (2, [8, 9]), ["clrjit.dll"] = (2, [10, 11]),
            [Host + ".deps.json"] = (3, Encoding.UTF8.GetBytes(Deps)),
            [Host + ".runtimeconfig.json"] = (4, Encoding.UTF8.GetBytes(Config)),
        };
        if (omit is not null) { files.Remove(omit); }
        if (extra is not null) { files.Add(extra, (1, [12])); }
        using var image = new MemoryStream();
        using var writer = new BinaryWriter(image, Encoding.UTF8, leaveOpen: true);
        image.SetLength(1024);
        image.Position = 0; writer.Write((ushort)0x5A4D);
        image.Position = 0x3c; writer.Write(0x80);
        image.Position = 0x80; writer.Write(0x4550); writer.Write((ushort)0x8664); writer.Write((ushort)1);
        image.Position = 0x94; writer.Write((ushort)240); writer.Write((ushort)0x22); writer.Write((ushort)0x20b);
        image.Position = 0xb8; writer.Write(0x140000000UL); writer.Write(4096); writer.Write(512);
        image.Position = 0xd0; writer.Write(8192); writer.Write(512);
        image.Position = 0xf4; writer.Write(16);
        image.Position = 0x188; writer.Write(Encoding.ASCII.GetBytes(".rdata\0\0"));
        writer.Write(512); writer.Write(4096); writer.Write(512); writer.Write(512);
        image.Position = 0x218;
        writer.Write(Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE"));
        image.Position = 1024;
        var entries = new List<(string Name, byte Type, long Offset, long Size, long Compressed)>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, file) in files)
        {
            hash.AppendData(SHA256.HashData(file.Bytes));
            var start = image.Position;
            var compress = compressed && name == "dependency.dll";
            if (compress) { using var deflate = new DeflateStream(image, CompressionLevel.Optimal, leaveOpen: true); deflate.Write(file.Bytes); }
            else { writer.Write(file.Bytes); }
            entries.Add((name, file.Type, start, file.Bytes.Length, compress ? image.Position - start : 0));
        }
        var header = image.Position;
        writer.Write(6U); writer.Write(0U); writer.Write(entries.Count);
        writer.Write(Convert.ToBase64String(hash.GetHashAndReset()).Replace('+', '-').Replace('/', '_')[..12]);
        foreach (var type in new byte[] { 3, 4 })
        {
            var entry = entries.SingleOrDefault(e => e.Type == type);
            writer.Write(entry.Offset); writer.Write(entry.Size);
        }
        writer.Write(0UL);
        foreach (var entry in entries)
        {
            writer.Write(entry.Offset); writer.Write(entry.Size); writer.Write(entry.Compressed);
            writer.Write(entry.Type); writer.Write(entry.Name);
        }
        image.Position = 0x210; writer.Write(header);
        return image.ToArray();
    }

    static void Verify(byte[] bytes, PackageDigest? digest = null) =>
        HostwBundle.Verify(new MemoryStream(bytes, writable: false), digest ?? Digest(bytes), Target);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_digest_matched_bundles_are_verified_without_sidecars_or_execution(bool compressed) => Verify(Bundle(compressed: compressed));

    [Theory]
    [InlineData("agent-bridge-hostw.dll")]
    [InlineData("agent-bridge-hostw.deps.json")]
    [InlineData("agent-bridge-hostw.runtimeconfig.json")]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("coreclr.dll")]
    [InlineData("clrjit.dll")]
    [InlineData("dependency.dll")]
    public void Missing_embedded_closure_is_rejected_even_with_a_matching_image_digest(string missing) =>
        Assert.Throws<InvalidDataException>(() => Verify(Bundle(omit: missing)));

    [Theory]
    [InlineData("header")]
    [InlineData("version")]
    [InlineData("range")]
    [InlineData("truncated")]
    [InlineData("content")]
    public void Malformed_partial_or_internally_mismatched_manifests_are_rejected(string damage)
    {
        var bytes = Bundle();
        var header = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x210)));
        switch (damage)
        {
            case "header": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x210), bytes.Length + 1); break;
            case "version": bytes[header] = 99; break;
            case "range": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(header + 65), header); break;
            case "truncated": bytes = bytes[..^5]; break;
            case "content": bytes[1024] ^= 0xff; break;
        }
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("AGENT-BRIDGE-HOSTW.DLL")]
    public void Unsafe_or_ambiguous_embedded_names_are_rejected(string extra) =>
        Assert.Throws<InvalidDataException>(() => Verify(Bundle(extra: extra)));

    [Fact]
    public void A_bundle_with_the_wrong_custody_digest_is_rejected() =>
        Assert.Throws<PackageMismatchException>(() => Verify(Bundle(), new(PackageDigest.Sha256, new string('0', 64))));
}
