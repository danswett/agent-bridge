using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using AgentBridge.Relay;
using AgentBridge.RelaySmoke;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace AgentBridge.Transport.Tests;

// What a production deployment relies on: the deploy workflow's smoke test, draining on a
// revision swap, logs without identities, and metrics with only low-cardinality tags.
public class RelayDeploymentTests
{
    [Fact]
    public async Task The_smoke_test_passes_against_a_working_relay_with_two_devices_talking_and_a_file_through_its_blob_store()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var relay = await TestRelay.StartAsync(o => { o.TokenKey = key; o.Version = "abc123"; o.Blobs.Backend = "local"; });
        var output = new StringWriter();

        var ok = await new Smoke(relay.Url, output) { TokenKey = key, AllowedOrigin = "https://pwa.example", ExpectVersion = "abc123", ExpectPush = true, ExpectBlobs = true }.RunAsync();

        Assert.True(ok, output.ToString());
        Assert.Contains("PASS two devices over a Noise channel", output.ToString());
        Assert.Contains("PASS a multi-MB file through the blob store", output.ToString());
    }

    [Fact]
    public async Task The_smoke_test_fails_when_a_blob_store_is_expected_and_the_relay_has_none()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var relay = await TestRelay.StartAsync(o => o.TokenKey = key);
        var output = new StringWriter();

        var ok = await new Smoke(relay.Url, output) { TokenKey = key, ExpectPush = true, ExpectBlobs = true }.RunAsync();

        Assert.False(ok);
        Assert.Contains("FAIL a multi-MB file through the blob store: the relay has no blob store", output.ToString());
    }

    [Fact]
    public async Task The_smoke_test_fails_when_the_expected_build_is_not_the_one_serving()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Version = "old");
        var output = new StringWriter();

        var ok = await new Smoke(relay.Url, output) { ExpectVersion = "new", StartupWait = TimeSpan.FromSeconds(1) }.RunAsync();

        Assert.False(ok);
        Assert.Contains("FAIL health and version", output.ToString());
    }

    [Fact]
    public async Task The_smoke_test_fails_against_a_relay_signing_with_another_key()
    {
        await using var relay = await TestRelay.StartAsync(o => o.TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var output = new StringWriter();

        var ok = await new Smoke(relay.Url, output) { TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ExpectPush = true }.RunAsync();

        Assert.False(ok);
        Assert.Contains("FAIL two devices over a Noise channel", output.ToString());
    }

    [Fact]
    public async Task A_stopping_relay_reports_not_ready_and_refuses_new_sockets_while_it_drains()
    {
        await using var relay = await TestRelay.StartAsync();
        var hub = relay.App.Services.GetService(typeof(RelayHub)) as RelayHub;
        var device = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "draining");
        Assert.Equal(HttpStatusCode.OK, (await relay.Http.GetAsync(new Uri(relay.Url, "/readyz"))).StatusCode);

        await hub!.DrainAsync();
        await device.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CloseCodes.Restarting, (int?)device.Relay.CloseStatus);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await relay.Http.GetAsync(new Uri(relay.Url, "/readyz"))).StatusCode);
        await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(() =>
            RelayClient.ConnectAsync(relay.Url, relay.TokenFor(1, "octo"), P256KeyPair.Generate(), "late", "test"));
        // Liveness stays up: a draining replica must not be restarted mid-drain.
        Assert.Equal(HttpStatusCode.OK, (await relay.Http.GetAsync(new Uri(relay.Url, "/healthz"))).StatusCode);
    }

    [Fact]
    public async Task Relay_logs_name_no_account_login_device_or_device_name()
    {
        await using var relay = await TestRelay.StartAsync();
        var account = await relay.SignInAsync(new GitHubUser(48151623, "octo-login"));
        var host = await relay.ConnectAsync(account.Token, "Dans-Laptop", "host");
        var app = await relay.ConnectAsync(account.Token, "Dans-Phone", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        await host.DisposeAsync();
        await Wait.UntilAsync(() => relay.Logs.Any(l => l.StartsWith("disconnect")), "the disconnect log line");

        var logs = string.Join("\n", relay.Logs);

        Assert.Contains("sign-in completed", logs);
        foreach (var identity in new[] { "octo-login", "48151623", "Dans-Laptop", "Dans-Phone", host.Id[..8], app.Id[..8] })
        {
            Assert.DoesNotContain(identity, logs);
        }
    }

    [Fact]
    public async Task A_relay_configured_for_JSON_logs_writes_JSON_lines()
    {
        await using var app = RelayApp.Build(["--Logging:Console:FormatterName=json"], builderHook: b => b.WebHost.UseUrls("http://127.0.0.1:0"));

        var console = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;

        Assert.Equal(ConsoleFormatterNames.Json, console.FormatterName);
        Assert.Contains(app.Services.GetServices<ConsoleFormatter>(), f => f.Name == ConsoleFormatterNames.Json);
    }

    [Fact]
    public async Task Relay_metrics_carry_only_allowlisted_low_cardinality_tags()
    {
        var seen = new List<(string Instrument, string Tag)>();
        // Other tests run relays in this process at the same time, on meters of the same name:
        // only this relay's measurements count.
        object? scope = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == RelayMetrics.MeterName) { l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => { if (i.Meter.Scope == Volatile.Read(ref scope)) { lock (seen) { foreach (var t in tags) { seen.Add((i.Name, t.Key)); } } } });
        listener.SetMeasurementEventCallback<int>((i, _, tags, _) => { if (i.Meter.Scope == Volatile.Read(ref scope)) { lock (seen) { foreach (var t in tags) { seen.Add((i.Name, t.Key)); } } } });
        listener.Start();
        await using var relay = await TestRelay.StartAsync(o => o.Blobs.Backend = "local");
        Volatile.Write(ref scope, relay.App.Services.GetRequiredService<IMeterFactory>());
        await relay.SignInAsync(new GitHubUser(1, "octo"));
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        var ch = await app.Hub.OpenAsync(host.Id);
        await ch.RequestAsync("ping");
        await app.Relay.PushAsync("https://push.example/x", [1]);
        await app.Relay.BlobPutAsync(host.Id, 4096);
        await Assert.ThrowsAsync<BlobRefusedException>(() => app.Relay.BlobGetAsync("not.a.ticket"));
        await Assert.ThrowsAsync<RelayRefusedException>(() => RelayClient.ConnectAsync(relay.Url, token + "x", P256KeyPair.Generate(), "x", "test"));
        await host.DisposeAsync();
        listener.RecordObservableInstruments();

        lock (seen)
        {
            Assert.Contains(seen, s => s.Instrument == "relay.connects");
            Assert.Contains(seen, s => s.Instrument == "relay.refusals");
            Assert.Contains(seen, s => s.Instrument == "relay.blobs");
            Assert.All(seen, s => Assert.Contains(s.Tag, RelayMetrics.AllowedTags));
        }
    }

    [Fact]
    public async Task Relay_metrics_can_be_collected_while_a_relay_shuts_down()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == RelayMetrics.MeterName) { l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<int>((_, _, _, _) => { });
        listener.Start();
        using var stop = new CancellationTokenSource();
        // An exporter collecting on its own schedule, as one does in production.
        var collecting = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) { listener.RecordObservableInstruments(); Thread.Yield(); }
        });

        try
        {
            for (var i = 0; i < 10 && !collecting.IsCompleted; i++)
            {
                await using var relay = await TestRelay.StartAsync();
                await relay.ConnectAsync(relay.TokenFor(1, "octo"), "host-a", "host");
            }
        }
        finally
        {
            stop.Cancel();
            await collecting;
        }
    }
}
