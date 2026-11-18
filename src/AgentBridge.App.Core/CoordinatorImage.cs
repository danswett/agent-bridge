using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace AgentBridge.App.Core;

internal static class CoordinatorImage
{
    static readonly UnicodeEncoding Unicode = new(false, true, true);

    internal static void RequireSlotEntryV1(Stream image, Func<Stream, string?>? metadata = null)
    {
        var description = metadata is null ? ReadDescription(image) : metadata(image);
        if (description?.EndsWith(" [slot-entry-v1] [coordinator-v1]", StringComparison.Ordinal) != true)
            throw new InvalidDataException("slot-entry-protocol-unavailable: helper lacks slot-entry-v1.");
    }

    internal static void RequireV1(Stream image, Func<Stream, string?>? metadata = null)
    {
        var description = metadata is null ? ReadDescription(image) : metadata(image);
        if (description?.EndsWith(" [coordinator-v1]", StringComparison.Ordinal) != true)
            throw new InvalidDataException("coordinator-protocol-unavailable: installed helper lacks coordinator-v1.");
    }

    internal static string? ReadDescription(Stream image)
    {
        var saved = image.Position;
        try
        {
            image.Position = 0;
            using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
            var resource = pe.PEHeaders.PEHeader?.ResourceTableDirectory
                ?? throw Bad("Missing PE header.");
            if (resource.RelativeVirtualAddress == 0 && resource.Size == 0) return null;
            // Native apphost resources include icons; retain the bundle verifier's section ceiling.
            if (resource.RelativeVirtualAddress <= 0 || resource.Size < 16 || resource.Size > 16 * 1024 * 1024)
                throw Bad("Invalid resource directory.");
            var bytes = pe.GetSectionData(resource.RelativeVirtualAddress).GetContent(0, resource.Size).ToArray();
            var visited = new HashSet<int>();
            string? description = null;
            var nodes = 0;

            void Range(int at, int length)
            {
                if (at < 0 || length < 0 || at > bytes.Length - length)
                    throw Bad("Resource range exceeds its directory.");
            }
            ushort U16(int at) { Range(at, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)); }
            uint U32(int at) { Range(at, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)); }
            int Align(int at) => checked((at + 3) & ~3);

            int Block(int at, int limit, int depth, int scope)
            {
                if (++nodes > 4096 || depth > 8 || at > limit - 6) throw Bad("Invalid version block nesting.");
                var length = U16(at);
                var valueLength = U16(at + 2);
                var type = U16(at + 4);
                if (length < 6 || length > limit - at || type > 1) throw Bad("Invalid version block.");
                var end = at + length;
                var keyAt = at + 6;
                var p = keyAt;
                while (p <= end - 2 && U16(p) != 0 && p - keyAt <= 512) p += 2;
                if (p > end - 2 || p - keyAt > 512) throw Bad("Unterminated version key.");
                var key = Unicode.GetString(bytes, keyAt, p - keyAt);
                var valueAt = Align(p + 2);
                var valueBytes = valueLength * (type == 1 ? 2 : 1);
                if (valueAt > end || valueBytes > end - valueAt) throw Bad("Truncated version value.");
                if (depth == 0 && (key != "VS_VERSION_INFO" || type != 0 || valueBytes != 52
                    || U32(valueAt) != 0xFEEF04BD || U32(valueAt + 4) != 0x00010000))
                    throw Bad("Invalid version root.");
                if (scope == 3 && key == "FileDescription")
                {
                    if (type != 1 || valueBytes < 2) throw Bad("Invalid native description.");
                    var text = Unicode.GetString(bytes, valueAt, valueBytes);
                    if (text[^1] != '\0' || text.IndexOf('\0') != text.Length - 1)
                        throw Bad("Invalid native description terminator.");
                    text = text[..^1];
                    if (description is not null && description != text) throw Bad("Conflicting native descriptions.");
                    description = text;
                }
                var childScope = depth == 0 ? 1
                    : scope == 1 && key == "StringFileInfo" ? 2
                    : scope == 2 && key.Length == 8 && key.All(Uri.IsHexDigit) ? 3 : -1;
                var child = Align(valueAt + valueBytes);
                while (child < end)
                {
                    var next = Block(child, end, depth + 1, childScope);
                    child = Align(next);
                }
                return end;
            }

            void Directory(int at, int depth)
            {
                Range(at, 16);
                if ((at & 3) != 0 || !visited.Add(at) || depth > 2)
                    throw Bad("Invalid resource directory nesting.");
                var count = U16(at + 12) + U16(at + 14);
                Range(at + 16, checked(count * 8));
                for (var i = 0; i < count; i++)
                {
                    var entry = at + 16 + i * 8;
                    if (depth == 0 && U32(entry) != 16) continue;
                    var target = U32(entry + 4);
                    var offset = checked((int)(target & 0x7fffffff));
                    if (depth < 2)
                    {
                        if ((target & 0x80000000) == 0) throw Bad("Missing version directory.");
                        Directory(offset, depth + 1);
                    }
                    else
                    {
                        if ((target & 0x80000000) != 0) throw Bad("Unexpected version directory.");
                        Range(offset, 16);
                        var relative = (long)U32(offset) - resource.RelativeVirtualAddress;
                        var size = U32(offset + 4);
                        if (relative < 0 || size < 6 || relative > bytes.Length - (long)size)
                            throw Bad("Version data exceeds its resource.");
                        Block((int)relative, checked((int)(relative + size)), 0, 0);
                    }
                }
            }
            Directory(0, 0);
            return description;
        }
        catch (Exception error) when (error is BadImageFormatException or ArgumentOutOfRangeException
            or EndOfStreamException or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("Malformed coordinator native version resource.", error);
        }
        finally { image.Position = saved; }
    }

    static InvalidDataException Bad(string reason) => new(reason);
}
