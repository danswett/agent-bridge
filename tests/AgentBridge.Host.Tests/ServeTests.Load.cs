using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Report = AgentBridge.Host.Core.Load.ProcessLoadReport;

public sealed partial class ServeTests
{
    readonly FakeTimeProvider loadTime = new(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));
    Report? loadReport;
    bool loadEnabled = true;

    LoadService ServeLoad()
    {
        var loads = new LoadService(() => loadReport, () => loadEnabled, time: loadTime);
        service = new(source, new("host-id", "Test host", "windows", "0.1.0"))
        { Load = loads, LoadEnabled = () => loadEnabled };
        host.Hub.Handler = service.HandleAsync;
        return loads;
    }

    Report Reading(double? cpu = 25) => new(loadTime.GetUtcNow(),
        new(4, cpu, 4096, TotalMemoryBytes: 8192), new Dictionary<string, AgentBridge.Host.Core.Load.SessionLoad>());

    [Fact]
    public async Task Load_get_maps_figures_omits_unknowns_and_bounds_escaped_records()
    {
        ServeLoad();
        var ch = await OpenAsync();
        loadReport = Reading();
        var record = Result(await ch.RequestAsync("load.get", new { }));
        Assert.Equal(25, record.GetProperty("cpu").GetProperty("percent").GetDouble());
        Assert.Equal(8192, record.GetProperty("memory").GetProperty("totalBytes").GetInt64());
        loadReport = Reading(null);
        Assert.False(Result(await ch.RequestAsync("load.get", new { })).TryGetProperty("cpu", out _));
        loadReport = Reading() with { Sessions = Enumerable.Range(0, 200).ToDictionary(
            n => n + new string('\u0001', 500), _ => new AgentBridge.Host.Core.Load.SessionLoad(LoadState.Ok, 1, 1, 1)) };
        record = Result(await ch.RequestAsync("load.get", new { }));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(record.GetRawText()) <= 64 * 1024);
        Assert.Equal("partial", record.GetProperty("state").GetString());
        loadReport = null;
        Assert.Equal("unavailable", Result(await ch.RequestAsync("load.get", new { })).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Load_subscription_is_event_driven_paced_replaced_and_stops_on_unsubscribe_or_close()
    {
        var loads = ServeLoad();
        var ch = await OpenAsync();
        loadReport = Reading();
        Assert.Equal(25, Result(await ch.RequestAsync("load.subscribe", new { })).GetProperty("cpu").GetProperty("percent").GetDouble());
        Result(await ch.RequestAsync("load.subscribe", new { }));
        Assert.Equal(1, loads.Subscribers);
        loads.Publish(loadReport);
        loadTime.Advance(TimeSpan.FromMilliseconds(999));
        loads.Publish(loadReport);
        await LoadQuietAsync(ch);
        loadTime.Advance(TimeSpan.FromMilliseconds(1));
        loadReport = Reading(42);
        loads.Publish(loadReport);
        var update = await NextAsync(ch);
        Assert.Equal(("load.update", 1L), (update.Name, update.Seq));
        Assert.Equal(42, update.Data.GetProperty("cpu").GetProperty("percent").GetDouble());
        loads.Publish(loadReport);
        await LoadQuietAsync(ch);
        Result(await ch.RequestAsync("load.unsubscribe", new { }));
        Assert.Equal(0, loads.Subscribers);
        loadTime.Advance(TimeSpan.FromSeconds(2));
        loads.Publish(loadReport);
        await LoadQuietAsync(ch);
        Result(await ch.RequestAsync("load.subscribe", new { }));
        loadEnabled = false;
        loadTime.Advance(TimeSpan.FromSeconds(2));
        loads.Publish(loadReport);
        await LoadQuietAsync(ch);
        await ch.DisposeAsync();
        await Fixture.EventuallyAsync(() => loads.Subscribers == 0);
    }

    static async Task LoadQuietAsync(SecureChannel ch)
    {
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    [Theory]
    [InlineData(TrustedPeerStore.Person, true)]
    [InlineData(TrustedPeerStore.Service, false)]
    [InlineData(TrustedPeerStore.Host, false)]
    public async Task Load_is_relay_only_enabled_only_and_person_only(string role, bool allowed)
    {
        ServeLoad();
        var store = new TrustedPeerStore(Path.Combine(f.Root, "load-trust"), host.Key.Public, () => host.Relay.AccountId, loadTime);
        store.Reset();
        store.Put(client.Key.Public, role, loadTime.GetUtcNow().AddHours(1));
        using var authorization = new RemoteAuthorization(true, store, loadTime);
        authorization.Attach(host.Hub);
        host.Hub.Handler = authorization.Handler(service);
        var ch = await OpenAsync();
        foreach (var op in new[] { "load.get", "load.subscribe", "load.unsubscribe" })
        { Assert.Equal(allowed, (await ch.RequestAsync(op, new { })).GetProperty("ok").GetBoolean()); }
        host.Hub.Handler = service.HandleAsync;
        loadEnabled = false;
        foreach (var op in new[] { "load.get", "load.subscribe", "load.unsubscribe", "load.update" })
        {
            Assert.Equal($"unknown op {op}", (await ch.RequestAsync(op, new { })).GetProperty("error").GetString());
            Assert.Null(await service.HandleAsync(new LoadPipe(), op, JsonSerializer.SerializeToElement(new { })));
        }
        loadEnabled = true;
        Assert.Null(await service.HandleAsync(new LoadPipe(), "load.get", JsonSerializer.SerializeToElement(new { })));
        Assert.Equal("unknown op load.update", (await ch.RequestAsync("load.update", new { })).GetProperty("error").GetString());
    }

    sealed class LoadPipe : IProtocolChannel
    {
        public string PeerId => "local-1";
        public string PeerName => "Local";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => new TaskCompletionSource().Task;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
