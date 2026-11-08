using System.Security.Cryptography;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentSignKeyTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("agent-sign-").FullName;

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void Nothing_is_made_while_the_mode_is_off_or_until_asked()
    {
        Assert.Null(AgentSignKey.Open(folder));
        Assert.Throws<InvalidOperationException>(() => AgentSignKey.OpenOrCreate(folder, modeOn: false));
        Assert.False(File.Exists(AgentSignKey.PathOf(folder)));
    }

    [Fact]
    public void The_key_is_made_once_and_the_same_public_point_is_reloaded()
    {
        using var first = AgentSignKey.OpenOrCreate(folder, true);
        using var second = AgentSignKey.OpenOrCreate(folder, true);
        using var reopened = AgentSignKey.Open(folder)!;
        Assert.Equal(65, first.PublicPoint.Length);
        Assert.Equal(first.PublicPoint, second.PublicPoint);
        Assert.Equal(first.PublicPoint, reopened.PublicPoint);
        Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentSignKey.FolderName)));
    }

    [Fact]
    public void The_key_signs_and_the_private_half_is_not_in_the_file_in_the_clear_on_windows()
    {
        using var key = AgentSignKey.OpenOrCreate(folder, true);
        var sig = key.Sign("hello"u8);
        using var verify = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = key.PublicPoint[1..33], Y = key.PublicPoint[33..] },
        });
        Assert.True(verify.VerifyData("hello"u8, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEqual(0x04, File.ReadAllBytes(AgentSignKey.PathOf(folder))[0]);
        }
    }

    [Fact]
    public void A_tampered_file_is_refused_and_not_replaced()
    {
        using (AgentSignKey.OpenOrCreate(folder, true)) { }
        var path = AgentSignKey.PathOf(folder);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => AgentSignKey.Open(folder));
        Assert.Throws<InvalidDataException>(() => AgentSignKey.OpenOrCreate(folder, true));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
