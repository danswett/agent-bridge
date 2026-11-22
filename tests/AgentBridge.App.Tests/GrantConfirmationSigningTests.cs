using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class GrantConfirmationSigningTests
{
    [Theory]
    [InlineData("grant.issue")]
    [InlineData("grant.revoke")]
    public void Grant_confirmation_authenticates_the_exact_args_and_destination(string action)
    {
        using var app = P256KeyPair.Generate();
        using var machine = P256KeyPair.Generate();
        using var args = JsonDocument.Parse("""{"generation":2,"digest":"fixture-digest"}""");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_793_300_000);
        var request = Guid.NewGuid();
        var command = Transfers.SignGrantConfirmation(app, machine.Public, action, args.RootElement, now, request);
        var body = BlobFormat.FromBase64Url(command.Body)!;
        var key = CommandMac.Key(machine, app.Public, Wire.DeviceId(app.Public), Wire.DeviceId(machine.Public));
        Assert.Equal(BlobFormat.Base64Url(HMACSHA256.HashData(key, body)), command.Mac);
        Assert.Equal(BlobFormat.Base64Url(app.Public), command.By);
        var read = JsonSerializer.Deserialize<CommandBody>(body, ProtocolJson.Options)!;
        Assert.Equal(action, read.Action);
        Assert.Equal(request.ToString("D"), read.Request);
        Assert.Equal(args.RootElement.GetRawText(), read.Args!.Value.GetRawText());
        Assert.Equal(("user", Wire.DeviceId(app.Public), Wire.DeviceId(machine.Public), (string?)null),
            (read.Principal.Kind, read.Principal.Device, read.Target.Machine, read.Target.Session));
        Assert.Equal(Wire.DeviceId(machine.Public), read.To);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("fixture-digest", "changed-digest"));
        Assert.NotEqual(command.Mac, BlobFormat.Base64Url(HMACSHA256.HashData(key, changed)));
    }

    [Theory]
    [InlineData("grant.read", "{}")]
    [InlineData("grant.issue", "null")]
    public void Nonconfirmation_actions_and_missing_args_cannot_be_signed(string action, string json)
    {
        using var app = P256KeyPair.Generate();
        using var machine = P256KeyPair.Generate();
        using var args = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => Transfers.SignGrantConfirmation(
            app, machine.Public, action, args.RootElement, DateTimeOffset.UtcNow, Guid.NewGuid()));
    }
}
