using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentBridge.AppleInterop;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

[CollectionDefinition("In-process loopback interop", DisableParallelization = true)]
public sealed class InProcessLoopbackInteropCollection;

[Collection("In-process loopback interop")]
public sealed class InteropTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    readonly IWebProxy previousProxy = HttpClient.DefaultProxy;

    public InteropTests()
    {
        // Program owns its process and applies this same isolation before startup.
        // Direct tests need it too, especially on Windows runners with a system proxy.
        HttpClient.DefaultProxy = new WebProxy();
    }

    public void Dispose() => HttpClient.DefaultProxy = previousProxy;

    [Fact]
    public async Task Direct_startup_still_refuses_a_proxy_that_does_not_bypass_loopback()
    {
        var isolatedProxy = HttpClient.DefaultProxy;
        var manifest = Path.Combine(Path.GetTempPath(), "abi-proxy-refusal-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            HttpClient.DefaultProxy = new RefusingProxy();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InteropStack.StartAsync(manifest, certificate: null));
            Assert.Equal("isolated interop requires loopback traffic to bypass all proxies", error.Message);
            Assert.False(File.Exists(manifest));
        }
        finally { HttpClient.DefaultProxy = isolatedProxy; }
    }

    sealed class RefusingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public bool IsBypassed(Uri host) => false;
        public Uri GetProxy(Uri destination) => throw new InvalidOperationException("the refused proxy must never be contacted");
    }

    [Fact]
    public async Task Real_local_and_noise_channels_share_the_parser_snapshot_and_live_updates_without_granting_host_management()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "abi-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        string root;
        await using (var stack = await InteropStack.StartAsync(manifest, certificate: null))
        {
            root = stack.Root;
            await using var local = await LocalClient.ConnectAsync(LocalEndpoint.For(root), Patience);
            var hello = await local.HelloAsync();
            Assert.Equal(stack.HostDeviceId, hello.Machine.Id);
            Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged], hello.Capabilities);
            Assert.Contains("Synthetic", hello.Machine.ReadOnly);
            await Assert.ThrowsAsync<HostErrorException>(() => local.CallAsync(HostOps.Stop));
            using var http = new HttpClient { Timeout = Patience };
            var account = await SignIn.DeviceFlowAsync(http, stack.RelayUrl, (code, uri) =>
            {
                Assert.Equal("TEST-ONLY", code);
                Assert.Equal("https://github.com/login/device", uri);
            });
            Assert.Equal(LocalGitHub.AccountId, account.AccountId);
            var key = P256KeyPair.Generate();
            await using var client = await RelayClient.ConnectAsync(stack.RelayUrl, account.Token, key, "Integration client", "app");
            var hub = new ChannelHub(client, key, "Integration client", "app") { JsonOptions = ProtocolJson.Options };
            await client.WaitForPeerAsync(p => p.Id == stack.HostDeviceId, Patience);
            var channel = await hub.OpenAsync(stack.HostDeviceId);
            var remoteHello = await Call<HelloResult>(channel, Ops.Hello);
            Assert.Equal(hello.Machine, remoteHello.Machine);
            var subscribed = await Call<SubscribeResult>(channel, Ops.SessionsSubscribe);
            Assert.Equal(1, subscribed.Count);
            using var deadline = new CancellationTokenSource(Patience);
            var first = await channel.Events.ReadAsync(deadline.Token);
            Assert.Equal(Events.SessionUpsert, first.Name);
            Assert.Contains(InteropStack.InitialMarker, first.Data.GetProperty("activity").GetProperty("response").GetString());
            Assert.Equal(Events.SessionsSynced, (await channel.Events.ReadAsync(deadline.Token)).Name);
            var full = await Call<Session>(channel, Ops.SessionGet, new { id = SyntheticSessions.SessionId });
            Assert.NotEmpty(full.Activity.History!);
            var detail = await Call<JsonElement>(channel, Ops.SessionGet, new { id = SyntheticSessions.SessionId, agent = SyntheticSessions.DelegateId });
            Assert.Equal(SyntheticSessions.DelegateId, detail.GetProperty("id").GetString());
            var trigger = stack.UpdateTriggerPath + ".pending";
            await File.WriteAllTextAsync(trigger, "advance");
            File.Move(trigger, stack.UpdateTriggerPath);
            var live = await channel.Events.ReadAsync(deadline.Token);
            Assert.Equal(Events.SessionUpsert, live.Name);
            Assert.Contains(InteropStack.UpdatedMarker, live.Data.GetProperty("activity").GetProperty("response").GetString());
            var refused = await Call<JsonElement>(channel, Ops.SessionReply, new { id = SyntheticSessions.SessionId, request = Guid.NewGuid().ToString(), text = "No real typing" });
            Assert.Equal("refused", refused.GetProperty("outcome").GetString());
            await stack.CommandAsync("reconnect");
            await client.WaitForPeerAsync(p => p.Id == stack.HostDeviceId, Patience);
            var next = await hub.OpenAsync(stack.HostDeviceId);
            Assert.Contains(InteropStack.UpdatedMarker, (await Call<Session>(next, Ops.SessionGet, new { id = SyntheticSessions.SessionId })).Activity.Response);
            await stack.CommandAsync("remove");
            Assert.Empty((await local.CallAsync<SessionList>(Ops.SessionsList)).Sessions);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(manifest));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
            }
        }
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task The_public_endpoint_uses_the_supplied_certificate_and_product_blob_routes_without_global_trust_changes()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel needs an imported key. Match the executable's PFX loading path.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), password: null);
        var manifest = Path.Combine(Path.GetTempPath(), "abi-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        await using var stack = await InteropStack.StartAsync(manifest, certificate);
        Assert.Equal("https", stack.RelayUrl.Scheme);
        Assert.Equal("localhost", stack.RelayUrl.Host);
        // The production client keeps its normal trust policy. Only this test HTTP observer
        // pins the single generated certificate, without changing system trust or app APIs.
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, errors) =>
                presented is not null && errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                && presented.RawData.SequenceEqual(certificate.RawData),
        }) { Timeout = Patience };
        Assert.True((await http.GetFromJsonAsync<JsonElement>(new Uri(stack.RelayUrl, "/healthz"))).GetProperty("ok").GetBoolean());
        var deviceKey = P256KeyPair.Generate();
        await using var device = await RelayClient.ConnectAsync(stack.InternalRelayUrl, stack.ClientToken, deviceKey, "Blob sender", "app");
        var recipientKey = P256KeyPair.Generate();
        await using var recipient = await RelayClient.ConnectAsync(stack.InternalRelayUrl, stack.ClientToken, recipientKey, "Blob recipient", "test");
        using var storage = BlobHttp.CreateClient();
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        var sent = await BlobTransfer.SendAsync(device, storage, recipient.DeviceId, bytes);
        using var received = new MemoryStream();
        var hash = await BlobTransfer.ReceiveAsync(recipient, storage, sent.Blob, sent.Key, bytes.Length, received);
        Assert.Equal(SHA256.HashData(bytes), hash);
        Assert.Equal(bytes, received.ToArray());
        await recipient.BlobDeleteAsync(sent.Blob);
    }

    [Fact]
    public async Task An_existing_manifest_is_never_overwritten_or_deleted_and_the_command_line_never_accepts_a_plaintext_password()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "abi-existing-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(manifest, "keep me");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => InteropStack.StartAsync(manifest, certificate: null));
            Assert.Equal("keep me", await File.ReadAllTextAsync(manifest));
            Assert.Throws<ArgumentException>(() => Arguments.Parse(["--certificate", "x", "--password", "secret", "--manifest", "y"]));
            Assert.Throws<ArgumentException>(() => Arguments.Parse(["--manifest", "y"]));
        }
        finally { File.Delete(manifest); }
    }

    [Fact]
    public async Task A_late_update_trigger_after_removal_does_not_prevent_listener_and_credential_cleanup()
    {
        var manifest = Path.Combine(Path.GetTempPath(), "abi-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        var stack = await InteropStack.StartAsync(manifest, certificate: null);
        var root = stack.Root;
        var relay = stack.RelayUrl;
        var socket = stack.LocalSocketPath;
        await stack.CommandAsync("remove");
        await File.WriteAllTextAsync(stack.UpdateTriggerPath, "advance");
        using var timeout = new CancellationTokenSource(Patience);
        while (File.Exists(stack.UpdateTriggerPath)) { await Task.Delay(10, timeout.Token); }
        await stack.DisposeAsync();
        Assert.False(File.Exists(manifest));
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(socket));
        using var closedListener = new TcpClient(AddressFamily.InterNetwork);
        using var connectionDeadline = new CancellationTokenSource(Patience);
        var refused = await Assert.ThrowsAsync<SocketException>(
            () => closedListener.ConnectAsync(IPAddress.Loopback, relay.Port, connectionDeadline.Token).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
    }

    static async Task<T> Call<T>(SecureChannel channel, string operation, object? data = null)
    {
        var response = await channel.RequestAsync(operation, data, timeout: Patience);
        Assert.True(response.GetProperty("ok").GetBoolean());
        return response.GetProperty("result").Deserialize<T>(ProtocolJson.Options)!;
    }
}
