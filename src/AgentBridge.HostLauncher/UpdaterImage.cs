using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.HostLauncher;

internal static class UpdaterImage
{
    sealed record Image(string Runtime, int Machine, int Size, string Sha256);
    sealed record Metadata(string SourceSha512, string IconSha256, Image[] Images);
    static readonly Metadata Trusted = ReadMetadata();
    internal static string SourceSha512 => Trusted.SourceSha512;
    internal static string IconSha256 => Trusted.IconSha256;

    static Metadata ReadMetadata()
    {
        using var resource = typeof(UpdaterImage).Assembly.GetManifestResourceStream("updater-images.json")
            ?? throw new InvalidDataException("updater image pins unavailable");
        return JsonSerializer.Deserialize<Metadata>(resource)
            ?? throw new InvalidDataException("updater image pins invalid");
    }

    internal static FileStream Pin(string path, string? runtime = null)
    {
        runtime ??= RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => ""
        };
        var pin = Trusted.Images.SingleOrDefault(p => p.Runtime == runtime)
            ?? throw new InvalidDataException("unsupported updater architecture");
        var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length < pin.Size || file.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("unsupported updater image size");
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            using var pe = new PEReader(new MemoryStream(bytes));
            if ((int)pe.PEHeaders.CoffHeader.Machine != pin.Machine || pe.PEHeaders.PEHeader is not { } header)
                throw new InvalidDataException("unsupported updater image");
            var optional = checked(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60)) + 24);
            var checksum = checked(optional + 64);
            var security = checked(optional + (header.Magic == PEMagic.PE32 ? 128 : 144));
            var certificate = header.CertificateTableDirectory;
            if (certificate.Size == 0 ? certificate.RelativeVirtualAddress != 0 || bytes.Length != pin.Size
                : certificate.RelativeVirtualAddress != pin.Size || certificate.Size < 8
                    || (long)pin.Size + certificate.Size != bytes.Length)
                throw new InvalidDataException("unsupported updater certificate extent");
            // Authenticode changes only these header fields and the appended certificate table.
            // Everything else, including resources, layout and code, is bound to the build pin.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(bytes.AsSpan(0, checksum));
            hash.AppendData(bytes.AsSpan(checksum + 4, security - checksum - 4));
            hash.AppendData(bytes.AsSpan(security + 8, pin.Size - security - 8));
            if (Convert.ToHexString(hash.GetHashAndReset()) != pin.Sha256)
                throw new InvalidDataException("unsupported updater image digest");
            file.Position = 0;
            return file;
        }
        catch (Exception error) when (error is BadImageFormatException or ArgumentOutOfRangeException or OverflowException)
        {
            file.Dispose();
            throw new InvalidDataException("malformed updater image", error);
        }
        catch { file.Dispose(); throw; }
    }
}
