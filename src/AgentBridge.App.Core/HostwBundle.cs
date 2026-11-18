using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PeMachine = System.Reflection.PortableExecutable.Machine;

namespace AgentBridge.App.Core;

internal static class HostwBundle
{
    const string Host = "agent-bridge-hostw";
    static readonly byte[] Signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
    sealed record Entry(long Offset, long Size, long Compressed, byte Type, string Name);

    internal static void Verify(Stream image, PackageDigest expected, string runtimeTarget)
    {
        if (!expected.Matches(image)) { throw new PackageMismatchException("Hostw bundle differs from its verified package digest."); }
        try { VerifyManifest(image, runtimeTarget); }
        catch (Exception e) when (e is EndOfStreamException or FormatException or DecoderFallbackException
            or JsonException or BadImageFormatException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("Malformed hostw bundle manifest or closure.", e); }
    }

    static void VerifyManifest(Stream image, string runtimeTarget)
    {
        image.Position = 0;
        using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
        var machine = runtimeTarget.EndsWith("/win-x64", StringComparison.Ordinal) ? PeMachine.Amd64
            : runtimeTarget.EndsWith("/win-arm64", StringComparison.Ordinal) ? PeMachine.Arm64 : throw Bad("Unsupported runtime target.");
        if (pe.PEHeaders.CoffHeader.Machine != machine || pe.PEHeaders.CorHeader is not null) { throw Bad("Hostw is not a matching native apphost."); }
        var optional = pe.PEHeaders.PEHeader ?? throw Bad("Missing PE header.");
        var alignment = optional.SectionAlignment;
        if (alignment < 1 || (alignment & (alignment - 1)) != 0 || optional.SizeOfHeaders < 1
            || optional.SizeOfHeaders > image.Length || optional.SizeOfImage < optional.SizeOfHeaders
            || optional.SizeOfImage % alignment != 0) { throw Bad("Invalid image extent or section alignment."); }
        long header = -1, hostEnd = optional.SizeOfHeaders, mappedEnd = hostEnd;
        foreach (var section in pe.PEHeaders.SectionHeaders.OrderBy(s => s.VirtualAddress))
        {
            if (section.SizeOfRawData < 0 || section.SizeOfRawData > 16 * 1024 * 1024 || section.PointerToRawData < 0
                || section.PointerToRawData > image.Length - section.SizeOfRawData) { throw Bad("Invalid apphost section."); }
            var mappedSize = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (section.VirtualSize < 0 || section.VirtualAddress < mappedEnd || section.VirtualAddress % alignment != 0
                || (long)section.VirtualAddress + mappedSize > optional.SizeOfImage) { throw Bad("Invalid mapped section extent."); }
            mappedEnd = (long)section.VirtualAddress + mappedSize;
            hostEnd = Math.Max(hostEnd, (long)section.PointerToRawData + section.SizeOfRawData);
            image.Position = section.PointerToRawData;
            var bytes = new byte[section.SizeOfRawData];
            image.ReadExactly(bytes);
            var at = bytes.AsSpan().IndexOf(Signature);
            if (at < 0) { continue; }
            if (at < 8 || header != -1 || bytes.AsSpan(at + 1).IndexOf(Signature) >= 0) { throw Bad("Ambiguous bundle marker."); }
            header = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at - 8));
        }
        if (header < hostEnd || header > image.Length - 65) { throw Bad("Missing or truncated bundle header."); }
        var export = optional.ExportTableDirectory;
        if (export.Size < 40) { throw Bad("Missing SDK static runtime exports."); }
        var directory = pe.GetSectionData(export.RelativeVirtualAddress).GetReader(0, 40);
        directory.Offset = 20; var functionCount = directory.ReadInt32(); var nativeCount = directory.ReadInt32(); HashSet<string> nativeNames = [];
        if (functionCount < 1 || functionCount > 10000 || nativeCount < 6 || nativeCount > 10000) { throw Bad("Invalid native runtime export count."); }
        var functions = pe.GetSectionData(directory.ReadInt32()).GetReader(0, functionCount * 4);
        var pointers = pe.GetSectionData(directory.ReadInt32()).GetReader(0, nativeCount * 4);
        var ordinals = pe.GetSectionData(directory.ReadInt32()).GetReader(0, nativeCount * 2);
        for (var i = 0; i < nativeCount; i++)
        {
            var ordinal = ordinals.ReadUInt16();
            if (ordinal >= functionCount) { throw Bad("Export ordinal exceeds its function table."); }
            functions.Offset = ordinal * 4; var address = functions.ReadInt32();
            if (address <= 0 || address >= optional.SizeOfImage
                || !pe.PEHeaders.SectionHeaders.Any(s => address >= s.VirtualAddress && (long)address - s.VirtualAddress < Math.Max(s.VirtualSize, s.SizeOfRawData))
                || address >= export.RelativeVirtualAddress && (long)address < (long)export.RelativeVirtualAddress + export.Size)
            { throw Bad("Export target is unmapped or forwarded outside the static runtime."); }
            var block = pe.GetSectionData(pointers.ReadInt32()); var name = block.GetReader(0, Math.Min(256, block.Length));
            nativeNames.Add(name.ReadUTF8(name.IndexOf(0)));
        }
        if (!new[] { "g_CLREngineMetrics", "CLRJitAttachState", "DotNetRuntimeInfo",
            "g_dacTable", "MetaDataGetDispenser", "DotNetRuntimeContractDescriptor" }.All(nativeNames.Contains))
        { throw Bad("Incomplete SDK statically linked runtime."); }
        image.Position = header;
        using var reader = new BinaryReader(image, new UTF8Encoding(false, true), leaveOpen: true);
        if (reader.ReadUInt32() != 6 || reader.ReadUInt32() != 0) { throw Bad("Unsupported bundle format; require .NET v6."); }
        var count = reader.ReadInt32();
        if (count < 1 || count > 10000) { throw Bad("Invalid embedded file count."); }
        var id = Text(reader);
        var depsLocation = (reader.ReadInt64(), reader.ReadInt64());
        var configLocation = (reader.ReadInt64(), reader.ReadInt64());
        if (id.Length != 12 || reader.ReadUInt64() != 0) { throw Bad("Invalid bundle identity or unsupported flags."); }
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<Entry>();
        long expanded = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = new Entry(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte(), Text(reader));
            expanded += entry.Size;
            if (entry.Size < 1 || entry.Size > 512L * 1024 * 1024 || expanded > 1024L * 1024 * 1024
                || entry.Compressed < 0 || entry.Type > 5 || !SlotPreparation.SafeRelative(entry.Name) || !entries.TryAdd(entry.Name, entry))
            { throw Bad("Invalid, unsafe or duplicate embedded entry."); }
            ordered.Add(entry);
        }
        var end = hostEnd;
        foreach (var entry in entries.Values.OrderBy(e => e.Offset))
        {
            var stored = entry.Compressed == 0 ? entry.Size : entry.Compressed;
            if (entry.Offset < end || entry.Offset > header || stored > header - entry.Offset) { throw Bad("Overlapping or partial embedded payload."); }
            end = entry.Offset + stored;
        }
        Entry Require(string name, byte type)
        {
            if (!entries.TryGetValue(name, out var entry) || entry.Type != type) { throw Bad("Missing embedded closure: " + name); }
            return entry;
        }
        var depsEntry = Require(Host + ".deps.json", 3);
        var configEntry = Require(Host + ".runtimeconfig.json", 4);
        if ((depsEntry.Offset, depsEntry.Size) != depsLocation || (configEntry.Offset, configEntry.Size) != configLocation
            || depsEntry.Compressed != 0 || configEntry.Compressed != 0) { throw Bad("JSON header locations do not match the manifest."); }
        Require(Host + ".dll", 1); Require("System.Private.CoreLib.dll", 1);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var documents = new Dictionary<byte, JsonElement>();
        var buffer = new byte[8192];
        foreach (var entry in ordered)
        {
            image.Position = entry.Offset;
            using var slice = new Slice(image, entry.Compressed == 0 ? entry.Size : entry.Compressed, entry.Compressed != 0);
            using Stream contents = entry.Compressed == 0 ? slice : new DeflateStream(slice, CompressionMode.Decompress, leaveOpen: true);
            if (entry.Type is 3 or 4 && (entry.Size > 4 * 1024 * 1024 || documents.ContainsKey(entry.Type))) { throw Bad("Ambiguous or oversized JSON closure."); }
            using var json = entry.Type is 3 or 4 ? new MemoryStream() : null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            int read;
            while ((read = contents.Read(buffer)) > 0)
            {
                size += read;
                if (size > entry.Size) { throw Bad("Expanded payload exceeds its manifest size."); }
                hash.AppendData(buffer, 0, read);
                json?.Write(buffer, 0, read);
            }
            if (size != entry.Size || slice.Remaining != 0) { throw Bad("Embedded payload is truncated."); }
            aggregate.AppendData(hash.GetHashAndReset());
            if (json is not null) { using var document = JsonDocument.Parse(json.ToArray(), new() { AllowDuplicateProperties = false }); documents.Add(entry.Type, document.RootElement.Clone()); }
        }
        var actualId = Convert.ToBase64String(aggregate.GetHashAndReset()).Replace('+', '-').Replace('/', '_')[..12];
        if (id != actualId) { throw Bad("Embedded bytes do not match the bundle manifest identity."); }
        var deps = documents[3];
        if (deps.GetProperty("runtimeTarget").GetProperty("name").GetString() != runtimeTarget) { throw Bad("Embedded runtime target differs from the package."); }
        // The SDK's embedded deps defines native closure; static apphost components have no separate entry.
        foreach (var (name, type) in SlotPreparation.RuntimeAssets(deps, runtimeTarget)) { Require(name, type); }
        var options = documents[4].GetProperty("runtimeOptions");
        if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _)
            || !options.GetProperty("includedFrameworks").EnumerateArray().Any(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App"))
        { throw Bad("Hostw bundle is not self-contained."); }
    }

    static InvalidDataException Bad(string message) => new("Hostw bundle: " + message);
    static string Text(BinaryReader reader)
    {
        var count = reader.Read7BitEncodedInt();
        if (count < 1 || count > 1024) { throw Bad("Invalid manifest string length."); }
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) { throw Bad("Truncated manifest string."); }
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    sealed class Slice(Stream source, long length, bool compressed) : Stream
    {
        public long Remaining { get; private set; } = length;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) { return 0; }
            // Prevent inflater read-ahead from hiding tails; exhaustion before BFINAL is not EOF success.
            if (compressed && Remaining == 0) { throw Bad("Compressed payload has no final block."); }
            var read = source.Read(buffer[..(int)Math.Min(buffer.Length, compressed ? Math.Min(1, Remaining) : Remaining)]);
            Remaining -= read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get => Length - Remaining; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
