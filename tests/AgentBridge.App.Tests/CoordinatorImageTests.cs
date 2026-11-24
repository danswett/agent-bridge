using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using AgentBridge.App.Core;
using Xunit;

public sealed class CoordinatorImageTests
{
    const string Supported = "fixture host launcher [coordinator-v1]";

    [Fact]
    public void The_native_description_is_read_from_the_retained_stream_without_closing_it()
    {
        using var image = new MemoryStream(Image(Supported), writable: false);
        Assert.Equal(Supported, CoordinatorImage.ReadDescription(image));
        CoordinatorImage.RequireV1(image);
        Assert.True(image.CanRead);
    }

    [Fact]
    public void Sdk_generated_PE_resources_are_read_without_executing_the_launcher()
    {
        var assembly = typeof(AgentBridge.HostLauncher.HostSupervisor).Assembly;
        var title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title
            ?? throw new InvalidDataException("The built launcher has no assembly title.");
        using var image = File.Open(assembly.Location, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(title, CoordinatorImage.ReadDescription(image));
    }

    [Fact]
    public void Native_icon_resources_may_exceed_one_MiB_without_hiding_the_version_marker()
    {
        var bytes = Image(Supported);
        const int resourceBytes = 1_358_076;
        Array.Resize(ref bytes, 0x400 + resourceBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), bytes.Length - 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x198), bytes.Length - 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x11c), resourceBytes);
        using var image = new MemoryStream(bytes, writable: true);
        Assert.Equal(Supported, CoordinatorImage.ReadDescription(image));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x11c), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => CoordinatorImage.ReadDescription(image));
    }

    [Theory]
    [InlineData(16 * 1024 * 1024)]
    [InlineData(32 * 1024 * 1024)]
    public void The_containing_raw_section_is_bounded_before_it_is_materialized(int sectionSize)
    {
        var bytes = Image(Supported);
        Array.Resize(ref bytes, 0x200 + sectionSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), sectionSize);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x198), sectionSize);
        using var image = new CountedStream(bytes);
        image.Position = 7;
        var error = Record.Exception(() => CoordinatorImage.RequireV1(image));
        if (sectionSize <= 16 * 1024 * 1024) { Assert.Null(error); }
        else
        {
            Assert.InRange(image.BytesRead, 0, 4096);
            Assert.IsType<InvalidDataException>(error);
        }
        Assert.Equal(7, image.Position);
        Assert.True(image.CanRead);
    }

    sealed class CountedStream(byte[] bytes) : Stream
    {
        readonly MemoryStream inner = new(bytes, writable: false);
        internal long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer);
            BytesRead += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fixture host launcher")]
    [InlineData("fixture host launcher [coordinator-v2]")]
    public void Missing_or_unsupported_cli_metadata_has_a_distinct_fail_closed_reason(string? description)
    {
        var bytes = Image(description ?? "fixture host launcher");
        if (description is null) { BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x118), 0); }
        using var image = new MemoryStream(bytes, writable: false);
        Assert.Equal(description, CoordinatorImage.ReadDescription(image));
        var error = Assert.Throws<InvalidDataException>(() => CoordinatorImage.RequireV1(image));
        Assert.Contains("coordinator-protocol-unavailable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_injection_receives_the_same_retained_image_not_a_resolved_path()
    {
        using var image = new MemoryStream(Image(Supported), writable: false);
        var calls = 0;
        CoordinatorImage.RequireV1(image, retained =>
        {
            calls++;
            Assert.Same(image, retained);
            Assert.True(retained.CanRead);
            return Supported;
        });
        Assert.Equal(1, calls);
        Assert.True(image.CanRead);
    }

    [Fact]
    public void Marker_text_in_an_overlay_is_not_a_native_version_resource()
    {
        var bytes = Image("fixture host launcher");
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x118), 0);
        using var image = new MemoryStream([.. bytes, .. Encoding.Unicode.GetBytes(Supported)], writable: false);
        Assert.Null(CoordinatorImage.ReadDescription(image));
        var error = Assert.Throws<InvalidDataException>(() => CoordinatorImage.RequireV1(image));
        Assert.Contains("coordinator-protocol-unavailable", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("directory-cycle")]
    [InlineData("truncated")]
    [InlineData("wrong-root")]
    public void Malformed_version_resource_offsets_and_blocks_are_rejected(string damage)
    {
        var bytes = Image(Supported);
        switch (damage)
        {
            case "range": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x448), int.MaxValue); break;
            case "directory-cycle": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x414), 0x80000000); break;
            case "truncated": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x44c), 2); break;
            case "wrong-root": bytes[0x486] = (byte)'X'; break;
        }
        using var image = new MemoryStream(bytes, writable: false);
        Assert.Throws<InvalidDataException>(() => CoordinatorImage.ReadDescription(image));
    }

    [Fact]
    public void Conflicting_native_descriptions_are_not_a_compatibility_proof()
    {
        using var image = new MemoryStream(Image(Supported, "different launcher"), writable: false);
        Assert.Throws<InvalidDataException>(() => CoordinatorImage.ReadDescription(image));
    }

    // Synthetic PE/resource bytes only. No fixture is written as or run as an executable.
    static byte[] Image(string description, string? duplicate = null)
    {
        var bytes = HostwBundleTests.Bundle()[..1024];
        Array.Resize(ref bytes, 2048);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), 1536);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x198), 1536);
        byte[] Text(string value) => Encoding.Unicode.GetBytes(value + "\0");
        var descriptions = new List<byte[]> { Block("FileDescription", Text(description)) };
        if (duplicate is not null) { descriptions.Add(Block("FileDescription", Text(duplicate))); }
        var table = Block("040904b0", [], children: descriptions.ToArray());
        var fixedInfo = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo, 0xFEEF04BD);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(4), 0x00010000);
        var version = Block("VS_VERSION_INFO", fixedInfo, binary: true,
            children: [Block("StringFileInfo", [], children: [table])]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x118), 0x1200);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x11c), 0x80 + version.Length);
        for (var level = 0; level < 3; level++)
        {
            var at = 0x400 + level * 24;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 14), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 16), level switch { 0 => 16, 1 => 1, _ => 1033 });
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 20),
                level < 2 ? 0x80000000U | (uint)((level + 1) * 24) : 72U);
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x448), 0x1280);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x44c), version.Length);
        version.CopyTo(bytes, 0x480);
        return bytes;
    }

    static byte[] Block(string key, byte[] value, bool binary = false, params byte[][] children)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write((ushort)0);
        writer.Write(checked((ushort)(binary ? value.Length : value.Length / 2)));
        writer.Write((ushort)(binary ? 0 : 1));
        writer.Write(Encoding.Unicode.GetBytes(key + "\0"));
        void Align() { while (stream.Position % 4 != 0) { writer.Write((byte)0); } }
        Align();
        writer.Write(value);
        foreach (var child in children) { Align(); writer.Write(child); }
        stream.Position = 0;
        writer.Write(checked((ushort)stream.Length));
        return stream.ToArray();
    }
}
