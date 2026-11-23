using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Theory]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("conin$.txt")]
    [InlineData("COM\u00b9")]
    [InlineData("lpt\u00b2.txt")]
    [InlineData("COM\u00b3.ext")]
    [InlineData("AGENTB~1.EXE")]
    [InlineData("ASSETS~12/resource.dat")]
    public void Console_devices_and_short_name_aliases_are_not_safe_payload_paths(string name)
    {
        // Check before extraction: a failed guard must not open a console device.
        var safe = typeof(SlotPreparation).GetMethod("SafeRelative", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(false, safe.Invoke(null, ["lib/app/" + name]));
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("file-size")]
    [InlineData("expanded-size")]
    public void Archive_limits_are_enforced_before_creating_staging(string limit)
    {
        MakePackage();
        if (limit == "entries")
        {
            using var zip = ZipFile.Open(Package, ZipArchiveMode.Update);
            for (var i = 0; i < 10000; i++) { zip.CreateEntry("ignored/" + i); }
        }
        else
        {
            var bytes = File.ReadAllBytes(Package);
            var at = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6)));
            for (var i = 0; i < (limit == "file-size" ? 1 : 3); i++)
            {
                var size = limit == "file-size" ? 512u * 1024 * 1024 + 1 : 400u * 1024 * 1024;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 24), size);
                at += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 30))
                    + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 32));
            }
            File.WriteAllBytes(Package, bytes);
        }
        Assert.Throws<InvalidDataException>(ReadPayload);
        Assert.False(Directory.Exists(Root));
    }

}
