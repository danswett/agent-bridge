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
        Assert.Single(LeftBehind());
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
    public void A_truncated_file_is_refused_with_a_clear_error_and_left_alone()
    {
        using (AgentSignKey.OpenOrCreate(folder, true)) { }
        var path = AgentSignKey.PathOf(folder);
        var half = File.ReadAllBytes(path)[..20];
        File.WriteAllBytes(path, half);
        Assert.Throws<InvalidDataException>(() => AgentSignKey.OpenOrCreate(folder, true));
        Assert.Equal(half, File.ReadAllBytes(path));
    }

    [Fact]
    public void Concurrent_first_callers_all_get_the_same_key_and_leave_no_temp_files()
    {
        var points = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8)
            .Select(_ => { using var k = AgentSignKey.OpenOrCreate(folder, true); return Convert.ToHexString(k.PublicPoint); })
            .ToList();
        Assert.Single(points.Distinct());
        Assert.Equal([AgentSignKey.FileName], LeftBehind());
    }

    [Fact]
    public async Task First_callers_in_separate_processes_all_get_the_same_key()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var assembly = typeof(Program).Assembly.Location;
        // The race is a narrow window, so several rounds of fresh folders make it near certain.
        for (var round = 0; round < 6; round++)
        {
            var profile = Path.Combine(folder, $"round{round}");
            var start = DateTime.UtcNow.AddSeconds(3).Ticks;
            var children = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                var info = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { assembly, "agent-sign-child", profile, start.ToString() }) { info.ArgumentList.Add(a); }
                using var child = System.Diagnostics.Process.Start(info)!;
                var error = child.StandardError.ReadToEndAsync();
                var output = await child.StandardOutput.ReadToEndAsync();
                await child.WaitForExitAsync();
                Assert.True(child.ExitCode == 0, await error);
                return output.Trim();
            })).ToList();
            var points = await Task.WhenAll(children);
            Assert.Single(points.Distinct());
            Assert.Equal(130, points[0].Length);
            Assert.Equal([AgentSignKey.FileName], LeftBehind(profile));
        }
    }

    // Files in the key's folder other than the lock file, which stays for the next caller.
    string[] LeftBehind(string? profile = null) => Directory.GetFiles(Path.Combine(profile ?? folder, AgentSignKey.FolderName))
        .Select(Path.GetFileName).Where(n => n != AgentSignKey.FileName + ".lock").ToArray()!;

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
