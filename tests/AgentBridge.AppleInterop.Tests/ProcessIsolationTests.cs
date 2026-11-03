using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentBridge.AppleInterop;
using AgentBridge.Transport;
using Xunit;

public sealed class ProcessIsolationTests
{
    [Fact]
    public async Task The_executable_ignores_ambient_proxy_cloud_store_and_listener_settings_for_authentication_and_host_startup()
    {
        var root = PrivateFiles.CreateRoot();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = Path.Combine(root, "server.pfx");
        var passwordFile = Path.Combine(root, "password");
        var manifest = Path.Combine(root, "manifest.json");
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
        PrivateFiles.WriteNew(passwordFile, password);
        var trap = new TcpListener(IPAddress.Loopback, 0);
        trap.Start();
        var proxy = $"http://127.0.0.1:{((IPEndPoint)trap.LocalEndpoint).Port}";
        var runtimeHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(runtimeHost)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in new[] { typeof(InteropStack).Assembly.Location, "--certificate", pfx, "--certificate-password-file", passwordFile, "--manifest", manifest })
        { start.ArgumentList.Add(arg); }
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) { start.Environment[name] = proxy; }
        start.Environment["NO_PROXY"] = start.Environment["no_proxy"] = "";
        start.Environment["Relay__Roster__Backend"] = "must-not-be-used";
        start.Environment["Relay__Blobs__Backend"] = "must-not-be-used";
        // If ambient Kestrel endpoints survived, startup would try to bind this occupied port.
        start.Environment["Kestrel__Endpoints__Ambient__Url"] = proxy;
        using var process = new Process { StartInfo = start };
        var launched = false;
        try
        {
            Assert.True(launched = process.Start());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.StartsWith("ready: isolated product relay and host", ready);
            using var data = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, deadline.Token));
            var relay = new Uri(data.RootElement.GetProperty("relayUrl").GetString()!);
            using var http = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) =>
                    presented is not null && errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                    && presented.RawData.SequenceEqual(certificate.RawData),
            });
            var account = await SignIn.DeviceFlowAsync(http, relay, (_, _) => { }, deadline.Token);
            Assert.Equal(LocalGitHub.AccountId, account.AccountId);
            Assert.False(trap.Pending(), "loopback traffic reached the trap proxy");
            await process.StandardInput.WriteLineAsync("quit");
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            var output = ready + await process.StandardOutput.ReadToEndAsync(deadline.Token) + await process.StandardError.ReadToEndAsync(deadline.Token);
            Assert.False(output.Contains(password, StringComparison.Ordinal), "the password appeared in process output");
            Assert.False(output.Contains(account.Token, StringComparison.Ordinal), "a token appeared in process output");
            Assert.False(File.Exists(manifest));
        }
        finally
        {
            if (launched && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            trap.Stop();
            Directory.Delete(root, recursive: true);
        }
    }
}
