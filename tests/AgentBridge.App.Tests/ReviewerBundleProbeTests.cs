using System.Buffers.Binary;
using System.Text;
using AgentBridge.App.Core;
using Xunit;

public sealed class ReviewerBundleProbeTests
{
    static void Verify(byte[] bytes) =>
        HostwBundle.Verify(new MemoryStream(bytes, false), HostwBundleTests.Digest(bytes), HostwBundleTests.Target);

    static (int Header, List<int> Entries) Locations(byte[] bytes)
    {
        var header = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x210)));
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        reader.BaseStream.Position = header + 8;
        var count = reader.ReadInt32();
        reader.ReadString();
        reader.BaseStream.Position += 40;
        var entries = new List<int>();
        for (var i = 0; i < count; i++)
        {
            entries.Add((int)reader.BaseStream.Position);
            reader.BaseStream.Position += 25;
            reader.ReadString();
        }
        return (header, entries);
    }

    [Fact]
    public void Export_names_without_any_exported_functions_do_not_prove_static_runtime_closure()
    {
        var bytes = HostwBundleTests.Bundle();
        foreach (var offset in new[] { 0x254, 0x25c, 0x264 }) { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), 0); }
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Fact]
    public void Invalid_export_function_and_ordinal_tables_fail_closed()
    {
        var bytes = HostwBundleTests.Bundle();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x254), 6);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x25c), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x264), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Theory]
    [InlineData(0x254, int.MaxValue)]
    [InlineData(0x25c, 0)]
    [InlineData(0x264, 0)]
    [InlineData(0x2b0, 6)]
    [InlineData(0x270, 0)]
    [InlineData(0x270, int.MaxValue)]
    [InlineData(0x270, 0x1fff)]
    [InlineData(0x270, 0x1040)]
    public void Export_tables_require_bounded_ordinals_and_in_image_nonforwarded_targets(int offset, int value)
    {
        var bytes = HostwBundleTests.Bundle();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Fact]
    public void Zero_initialized_runtime_data_exports_are_in_the_loaded_image()
    {
        var bytes = HostwBundleTests.Bundle();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), 1024);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x274), 0x1200);
        Verify(bytes);
    }

    [Theory]
    [InlineData("outside-image")]
    [InlineData("overflow")]
    [InlineData("negative-size")]
    [InlineData("overlap")]
    [InlineData("unaligned")]
    [InlineData("zero-alignment")]
    public void Invalid_mapped_section_extents_fail_closed(string damage)
    {
        var bytes = HostwBundleTests.Bundle();
        switch (damage)
        {
            case "outside-image": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), 0x1001); break;
            case "overflow": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), int.MaxValue); break;
            case "negative-size": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), -1); break;
            case "zero-alignment": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xb8), 0); break;
            default:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 2);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x1b8), 512);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x1bc), damage == "overlap" ? 0x1000 : 0x1800);
                break;
        }
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Fact]
    public void An_aligned_separate_zero_fill_section_can_supply_runtime_data()
    {
        var bytes = HostwBundleTests.Bundle();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xd0), 0x3000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x1b8), 512);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x1bc), 0x2000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x274), 0x2000);
        Verify(bytes);
    }

    [Fact]
    public void Raw_file_padding_cannot_supply_a_runtime_export()
    {
        var bytes = HostwBundleTests.Bundle();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x190), 511);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x274), 0x11ff);
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Fact]
    public void A_deflate_stream_without_its_final_block_is_rejected()
    {
        var bytes = HostwBundleTests.Bundle(compressed: true);
        var (_, entries) = Locations(bytes);
        var compressedOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(entries[2])));
        Assert.Equal(1, bytes[compressedOffset] & 1);
        bytes[compressedOffset] &= 0xfe;
        Assert.Throws<InvalidDataException>(() => Verify(bytes));
    }

    [Fact]
    public void Bytes_after_the_final_deflate_block_are_rejected() =>
        Assert.Throws<InvalidDataException>(() => Verify(HostwBundleTests.Bundle(compressed: true, trailingCompressed: true)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cyclic_dependency_graphs_fail_closed(bool self)
    {
        var deps = self
            ? """{"runtimeTarget":{"name":"net10/win-x64"},"targets":{"net10/win-x64":{"hostw/1":{"dependencies":{"hostw":"1"}}}}}"""
            : """{"runtimeTarget":{"name":"net10/win-x64"},"targets":{"net10/win-x64":{"hostw/1":{"dependencies":{"child":"1"}},"child/1":{"dependencies":{"hostw":"1"}}}}}""";
        Assert.Throws<InvalidDataException>(() => Verify(HostwBundleTests.Bundle(deps: deps)));
    }

    [Theory]
    [InlineData("duplicate-deps")]
    [InlineData("duplicate-target")]
    [InlineData("duplicate-config")]
    public void Ambiguous_dependency_documents_fail_closed(string damage)
    {
        var deps = damage == "duplicate-config" ? null : damage == "duplicate-deps"
            ? """{"runtimeTarget":{"name":"net10/win-x64"},"targets":{"net10/win-x64":{"hostw/1":{"dependencies":{"absent":"1"},"dependencies":{}}}}}"""
            : """{"runtimeTarget":{"name":"net10/win-arm64","name":"net10/win-x64"},"targets":{"net10/win-x64":{}}}""";
        var config = damage == "duplicate-config"
            ? """{"runtimeOptions":{"includedFrameworks":[],"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}""" : null;
        Assert.Throws<InvalidDataException>(() => Verify(HostwBundleTests.Bundle(deps: deps, config: config)));
    }

    [Fact]
    public void A_closed_acyclic_dependency_graph_remains_valid() =>
        Verify(HostwBundleTests.Bundle(deps: """{"runtimeTarget":{"name":"net10/win-x64"},"targets":{"net10/win-x64":{"hostw/1":{"dependencies":{"child":"1"}},"child/1":{}}}}"""));
}
