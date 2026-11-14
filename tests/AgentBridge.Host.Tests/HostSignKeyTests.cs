using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

namespace AgentBridge.Host.Tests;

[SupportedOSPlatform("windows")]
public sealed class HostSignKeyTests : IDisposable
{
    readonly string profile = Directory.CreateTempSubdirectory("host-sign-").FullName;
    string Store => Path.Combine(profile, "host-sign");
    const string Sentinel = "must-not-appear-in-diagnostics";

    public void Dispose() => Directory.Delete(profile, true);

    [WindowsFact("hostSign custody uses Windows TPM or DPAPI")]
    public async Task The_same_private_key_survives_reopen_in_a_new_process_and_is_not_the_AP5_key()
    {
        byte[] point;
        using (var first = HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi)) { point = first.PublicPoint; }
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { typeof(Program).Assembly.Location, "host-sign-open", profile, Convert.ToHexString(SHA256.HashData(point)) })
        { info.ArgumentList.Add(arg); }
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
        Assert.True(child.ExitCode == 0, "the fresh process did not reopen the committed identity");
        Assert.True((await output).Length == 0 && (await error).Length == 0, "custody emitted diagnostics");
        using var again = HostSignKey.LoadOrCreate(profile);
        using var ap5 = AgentSignKey.OpenOrCreate(profile, true);
        Assert.True(point.AsSpan().SequenceEqual(again.PublicPoint), "the persisted identity changed");
        Assert.False(point.AsSpan().SequenceEqual(ap5.PublicPoint), "the AP5-lite key was reused");
        Assert.True(AgentBridge.Roster.P256.Verify(point, "custody probe"u8, again.Sign("custody probe"u8.ToArray())));
    }

    [WindowsHostSignTheory]
    [InlineData("reference")]
    [InlineData("private")]
    [InlineData("commit")]
    [InlineData("missing-reference")]
    [InlineData("missing-commit")]
    [InlineData("mismatched-commit")]
    public void Corrupt_or_incomplete_state_is_refused_without_replacing_files_or_echoing_contents(string damage)
    {
        using (HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi)) { }
        var reference = Path.Combine(Store, "signing-key.json");
        var commit = Path.Combine(Store, "host-sign.public");
        switch (damage)
        {
            case "reference": File.WriteAllText(reference, JsonSerializer.Serialize(new { Storage = Sentinel, Name = Sentinel })); break;
            case "private": File.WriteAllText(Path.Combine(Store, "signing-key.dpapi"), Sentinel); break;
            case "commit": File.WriteAllText(commit, Sentinel); break;
            case "missing-reference": File.Delete(reference); break;
            case "missing-commit": File.Delete(commit); break;
            case "mismatched-commit":
                using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    var q = other.ExportParameters(false).Q;
                    File.WriteAllBytes(commit, [0x04, .. q.X!, .. q.Y!]);
                }
                break;
        }
        var before = Directory.GetFiles(Store).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        var failure = Assert.Throws<InvalidDataException>(() => HostSignKey.LoadOrCreate(profile));
        Assert.False(failure.ToString().Contains(Sentinel, StringComparison.Ordinal), "stored contents escaped in diagnostics");
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Store).Select(Path.GetFileName).Order());
        foreach (var (name, bytes) in before)
        { Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Store, name!))), "refused state was rewritten"); }
    }

    [WindowsHostSignTheory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("wrong-tier")]
    public void OpenExisting_never_creates_or_repairs_and_uses_one_content_free_refusal(string damage)
    {
        if (damage != "missing") { using var made = HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi); }
        if (damage == "wrong-tier") { File.WriteAllText(Path.Combine(Store, "signing-key.json"), "{\"Storage\":\"untrusted-tier\",\"Name\":null}"); }
        var contents = Directory.GetFiles(profile, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        using var locked = damage == "unreadable"
            ? new FileStream(Path.Combine(Store, "host-sign.public"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var before = Directory.GetFileSystemEntries(profile, "*", SearchOption.AllDirectories).Order().ToArray();
        var failure = Assert.Throws<InvalidDataException>(() => HostSignKey.OpenExisting(profile));
        Assert.Equal("missing-key", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(before, Directory.GetFileSystemEntries(profile, "*", SearchOption.AllDirectories).Order().ToArray());
        locked?.Dispose();
        Assert.True(contents.All(p => p.Value.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(p.Key)))), "refused custody was modified");
    }

    public sealed class WindowsHostSignTheoryAttribute : TheoryAttribute
    {
        public WindowsHostSignTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) { Skip = "hostSign custody uses Windows TPM or DPAPI"; }
        }
    }

    [WindowsFact("hostSign custody uses Windows TPM or DPAPI")]
    public void Private_material_is_encrypted_in_a_private_folder_and_not_returned_as_diagnostics()
    {
        using var key = HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi);
        Assert.True(PrivateFolder.IsPrivate(Store), "the key folder is not owner-only");
        var ciphertext = File.ReadAllBytes(Path.Combine(Store, "signing-key.dpapi"));
        var clear = ProtectedData.Unprotect(ciphertext, Encoding.UTF8.GetBytes("agent-bridge-roster-signing-key-v1"), DataProtectionScope.CurrentUser);
        byte[]? scalar = null;
        try
        {
            using var imported = ECDsa.Create();
            imported.ImportPkcs8PrivateKey(clear, out _);
            scalar = imported.ExportParameters(true).D!;
            Assert.True(ciphertext.AsSpan().IndexOf(clear) < 0 && ciphertext.AsSpan().IndexOf(scalar) < 0, "private material is plaintext on disk");
            var diagnostics = JsonSerializer.Serialize(key) + key;
            Assert.False(diagnostics.Contains(Convert.ToBase64String(scalar), StringComparison.Ordinal), "private material was returned");
            Assert.False(diagnostics.Contains(Convert.ToHexString(scalar), StringComparison.Ordinal), "private material was returned");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (scalar is not null) { CryptographicOperations.ZeroMemory(scalar); }
        }
    }

    [WindowsFact("hostSign custody uses Windows TPM or DPAPI")]
    public void Concurrent_first_callers_commit_one_identity()
    {
        var points = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(_ =>
        {
            using var key = HostSignKey.LoadOrCreate(profile, KeyStorage.Dpapi);
            return Convert.ToHexString(SHA256.HashData(key.PublicPoint));
        }).ToArray();
        Assert.Single(points.Distinct());
    }
}
