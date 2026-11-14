using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class HostSigningStatusTests : IDisposable
{
    readonly Fixture fixture = new();
    const string Sentinel = "private-custody-contents-must-not-escape";
    string Store => Path.Combine(fixture.Root, "host-sign");
    public void Dispose() => fixture.Dispose();

    HostControl Control() => new(new HostControlOptions
    {
        Profile = "test", Folder = fixture.Root, Version = "0.1.0", Running = new(false),
        Machine = new("machine", "Fixture", "windows", "0.1.0"),
        SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => { },
        Stop = () => { }, Restart = () => { },
    });

    static JsonElement Metadata(HostControl control)
    {
        var status = JsonSerializer.SerializeToElement(control.Status(), ProtocolJson.Options);
        Assert.True(status.TryGetProperty("hostSigning", out var custody),
            "host.status must expose hostSigning custody metadata");
        return custody;
    }

    static string[] Fields(JsonElement value) => value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    static async Task AssertUnsignedAsync()
    {
        var sent = new List<JsonElement>();
        var logs = new List<string>();
        var publication = new FleetPublication(new FleetPublisherTests.Source(), "machine", share: true, log: logs.Add);
        Assert.True(await publication.PollAsync((record, _) =>
        {
            sent.Add(record.Clone());
            return Task.CompletedTask;
        }));
        var frame = Assert.Single(sent);
        Assert.Equal(1, frame.GetProperty("v").GetInt32());
        Assert.False(frame.TryGetProperty("sig", out _));
        Assert.DoesNotContain(Sentinel, frame.GetRawText() + string.Join("|", logs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_damaged_custody_is_reported_without_creating_repairing_or_signing(bool damaged)
    {
        if (damaged)
        {
            Directory.CreateDirectory(Store);
            File.WriteAllText(Path.Combine(Store, "host-sign.public"), Sentinel);
        }
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var status = Metadata(Control());
        Assert.Equal("missing-key", status.GetProperty("result").GetString());
        Assert.Equal(["result"], Fields(status));
        Assert.DoesNotContain(Sentinel, status.GetRawText());
        await AssertUnsignedAsync();
        Assert.Equal(damaged, Directory.Exists(Store));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [WindowsFact("hostSign custody uses Windows TPM or DPAPI")]
    [SupportedOSPlatform("windows")]
    public async Task Available_metadata_identifies_the_actual_reopened_signing_key_without_enabling_publication()
    {
        byte[] point;
        using (var created = HostSignKey.LoadOrCreate(fixture.Root, KeyStorage.Dpapi)) { point = created.PublicPoint; }
        using var key = HostSignKey.OpenExisting(fixture.Root);
        Assert.True(P256.Verify(point, "metadata probe"u8, key.Sign("metadata probe"u8.ToArray())));
        var expectedId = Codec.Base64Url(SHA256.HashData(point));
        var first = Metadata(Control());
        var reopened = Metadata(Control());
        Assert.Equal("available", first.GetProperty("result").GetString());
        Assert.Equal("dpapi", first.GetProperty("storage").GetString());
        Assert.Equal(expectedId, first.GetProperty("keyId").GetString());
        Assert.Equal(["keyId", "result", "storage"], Fields(first));
        Assert.Equal(first.GetRawText(), reopened.GetRawText());
        await AssertUnsignedAsync();
    }

    [WindowsFact("hostSign custody uses Windows TPM or DPAPI")]
    [SupportedOSPlatform("windows")]
    public async Task Key_loss_is_reported_on_the_same_control_without_a_cached_identity_or_replacement()
    {
        using (HostSignKey.LoadOrCreate(fixture.Root, KeyStorage.Dpapi)) { }
        var control = Control();
        Assert.Equal("available", Metadata(control).GetProperty("result").GetString());
        var path = Path.Combine(Store, "signing-key.dpapi");
        File.WriteAllText(path, Sentinel);
        var status = Metadata(control);
        Assert.Equal("missing-key", status.GetProperty("result").GetString());
        Assert.Equal(["result"], Fields(status));
        Assert.DoesNotContain(Sentinel, status.GetRawText());
        Assert.Equal(Sentinel, File.ReadAllText(path));
        await AssertUnsignedAsync();
    }
}
