using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using AgentBridge.App.Core;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Fact]
    public async Task Eight_concurrent_preparations_validate_the_winner_and_return_the_same_receipt()
    {
        MakePackage();
        var digest = Digest;
        var legacy = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "current")).FullName, "running.exe");
        var pointer = Path.Combine(Root, "active-slot.json");
        File.WriteAllText(legacy, "running legacy");
        File.WriteAllText(pointer, "existing pointer");
        using var pinLegacy = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pinPointer = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var ready = new Barrier(8);
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(
            () => SlotPreparation.Prepare(Root, Version, Package, digest, version =>
            {
                if (!ready.SignalAndWait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("Preparation callers did not rendezvous."); }
                return version;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var receipts = await Task.WhenAll(callers);
        Assert.All(receipts, receipt => Assert.Equal(receipts[0], receipt));
        Assert.Equal(receipts[0], Prepare());
        Assert.Single(Directory.GetDirectories(Path.Combine(Root, "slots")));
        Assert.Equal("existing pointer", File.ReadAllText(pointer));
        Assert.Equal("running legacy", File.ReadAllText(legacy));
    }

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
        Assert.Throws<InvalidDataException>(() => Prepare());
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void An_existing_slot_with_an_extra_empty_directory_is_refused_without_repair()
    {
        MakePackage();
        Prepare();
        var extra = Directory.CreateDirectory(Path.Combine(Slot, "Assets", "unexpected")).FullName;
        Assert.Throws<InvalidDataException>(() => Prepare());
        Assert.True(Directory.Exists(extra));
        Assert.False(File.Exists(Path.Combine(Root, "active-slot.json")));
        Assert.False(Directory.Exists(Path.Combine(Root, "current")));
    }
}
