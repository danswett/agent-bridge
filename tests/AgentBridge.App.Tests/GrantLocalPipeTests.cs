using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.AgentEnvelope;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Grants;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed partial class GrantWireReceiverTests
{
    [Fact]
    public async Task Local_pipe_carries_retained_contexts_binary_proofs_and_required_issue_receipts_without_generic_fallback()
    {
        using var wire = new Dispatcher("rotated");
        using var app = P256KeyPair.Generate();
        var person = new Signer(app, () => Assert.Fail("Reading must not sign"));
        var channel = new GrantChannel(wire);
        await using var connection = Connection(channel);
        var local = new LocalGrantWireDispatcher(connection, wire.Binding.Receiver, "default");
        connection.Start(); await connection.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var receiver = new GrantWireReceiver(local, wire.Binding.Receiver, "default", () => person);
        var read = await ((IVerifiedGrantReader)receiver).ReadAsync();
        Assert.Equal(FleetGrantReadProblem.RotationRequired, read.Problem);
        Assert.True(read.G7Ready);
        Assert.Equal(new[] { "grant.binding", "grant.read", "grant.binding" }, channel.GrantCalls.Select(c => c.Op));
        Assert.All(channel.GrantCalls, c => Assert.Equal(TimeSpan.FromSeconds(30), c.Timeout));
        var context = new FleetGrantProofContext(wire.Binding.Account, "default", wire.Binding.Receiver, "grant.confirmIssue", Guid.NewGuid());
        var args = new FleetGrantConfirmation(Guid.NewGuid(), Guid.NewGuid(), 1, new('a', 64));
        var command = Transfers.SignGrantConfirmation(app, Codec.FromBase64Url(wire.Binding.ReceiverKey, "ReceiverKey"), context, args, DateTimeOffset.UtcNow);
        var call = new FleetGrantCall(context, JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
            JsonSerializer.SerializeToElement(command, ProtocolJson.Options));
        var receipt = await local.ConfirmIssueAsync(call, default);
        Assert.True(FleetGrantReceiptProof.Verify(receipt, Codec.FromBase64Url(wire.Binding.HostSign, "HostSign"), context).GetProperty("rotated").GetBoolean());
        Assert.Same(call, channel.GrantCalls[^1].Data);
        await Assert.ThrowsAsync<IOException>(() => local.CallAsync(call with { Context = context with { Action = "grant.prepareIssue" } }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => local.ConfirmIssueAsync(call with { Context = context with { Profile = "other" } }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => local.BindAsync(new(wire.Binding.Receiver, "other", Guid.NewGuid()), default));
        Assert.Equal(4, channel.GrantCalls.Count);
    }

    [Theory]
    [InlineData("{\"reply\":{\"problem\":\"none\"},\"proof\":{\"body\":\"e30\",\"signature\":\"AA\"},\"extra\":1}")]
    [InlineData("{\"reply\":{\"problem\":\"none\"},\"reply\":{\"problem\":\"none\"},\"proof\":{\"body\":\"e30\",\"signature\":\"AA\"}}")]
    [InlineData("{\"Reply\":{\"problem\":\"none\"},\"proof\":{\"body\":\"e30\",\"signature\":\"AA\"}}")]
    [InlineData("{\"reply\":{\"problem\":\"none\"}}")]
    public async Task Local_pipe_rejects_malformed_typed_packets_without_a_second_request(string raw)
    {
        using var wire = new Dispatcher("none");
        var channel = new GrantChannel(wire) { RawRead = raw };
        await using var connection = Connection(channel);
        connection.Start(); await connection.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var local = new LocalGrantWireDispatcher(connection, wire.Binding.Receiver, "default");
        var context = new FleetGrantProofContext(wire.Binding.Account, "default", wire.Binding.Receiver, "grant.read", Guid.NewGuid());
        var error = await Record.ExceptionAsync(() => local.ReadProblemAsync(new(context,
            JsonSerializer.SerializeToElement(new FleetGrantReadRequest(), ProtocolJson.Options)), default));
        Assert.True(error is JsonException or InvalidDataException, error?.GetType().Name ?? "accepted");
        Assert.Single(channel.GrantCalls);
    }

    [Fact]
    public async Task Local_pipe_discards_a_late_reply_when_the_connected_receiver_changes_without_retrying()
    {
        using var wire = new Dispatcher("none");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new GrantChannel(wire) { ReadHeld = held };
        await using var connection = Connection(channel);
        connection.Start(); await connection.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var local = new LocalGrantWireDispatcher(connection, wire.Binding.Receiver, "default");
        var context = new FleetGrantProofContext(wire.Binding.Account, "default", wire.Binding.Receiver, "grant.read", Guid.NewGuid());
        var reading = local.ReadProblemAsync(new(context, JsonSerializer.SerializeToElement(new FleetGrantReadRequest(), ProtocolJson.Options)), default);
        channel.Announce(new('b', 32));
        await Wait.UntilAsync(() => connection.Hello?.Machine.Id == new string('b', 32), "the receiver changed while a read was held");
        held.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => reading);
        Assert.Single(channel.GrantCalls);
    }

    [Fact]
    public async Task Local_pipe_binding_keeps_the_model_on_reconnect_never_reads_on_notifications_and_detaches_on_quit()
    {
        using var wire = new Dispatcher("rotation");
        using var app = P256KeyPair.Generate();
        var person = new Signer(app, () => Assert.Fail("Connection changes must not sign"));
        var first = new GrantChannel(wire); var second = new GrantChannel(wire);
        var connects = 0;
        await using var connection = new HostConnection(ct => Task.FromResult<IClientChannel>(++connects == 1 ? first : second),
            new LockedDispatcher()) { Subscribes = false, RetryDelay = TimeSpan.FromMilliseconds(20) };
        var bindings = 0; var offline = 0; GrantListViewModel? model = null;
        using var binding = new LocalGrantBinding(connection, "default", () => person,
            receiver => { bindings++; model = new GrantListViewModel(receiver); },
            () => { offline++; _ = model!.RefreshAsync(); });
        connection.Start(); await connection.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, bindings); Assert.Empty(first.GrantCalls);
        await model!.RefreshAsync(); Assert.True(model.CanIssueFirst);
        var retained = model;
        first.Announce(first.Receiver);
        await Wait.UntilAsync(() => first.Hellos == 2, "the unchanged hello processed");
        Assert.Equal(1, bindings); Assert.Equal(3, first.GrantCalls.Count);
        await first.DisposeAsync();
        await Wait.UntilAsync(() => connection.ConnectionNumber == 2 && connection.State == LinkState.Connected, "the in-memory reconnect");
        Assert.Equal(1, offline); Assert.False(model.CanIssueFirst); Assert.Same(retained, model);
        Assert.Empty(second.GrantCalls);
        second.Announce(new('b', 32));
        await Wait.UntilAsync(() => bindings == 2, "a genuinely different local receiver bound");
        Assert.NotSame(retained, model); Assert.False(model.CanIssueFirst); Assert.Empty(second.GrantCalls);
        binding.Dispose();
        second.Announce(new('c', 32));
        await Wait.UntilAsync(() => connection.Hello?.Machine.Id == new string('c', 32), "the detached connection updated");
        Assert.Equal(2, bindings); Assert.Equal(1, offline);
    }

    static HostConnection Connection(GrantChannel channel) => new(
        ct => Task.FromResult<IClientChannel>(channel), new LockedDispatcher()) { Subscribes = false };

    sealed class GrantChannel(Dispatcher wire) : IClientChannel
    {
        readonly Channel<ChannelEvent> events = Channel.CreateUnbounded<ChannelEvent>();
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long sequence;
        int revision;
        public string Receiver { get; private set; } = wire.Binding.Receiver;
        public string? RawRead { get; init; }
        public TaskCompletionSource? ReadHeld { get; init; }
        public int Hellos { get; private set; }
        public List<(string Op, object? Data, TimeSpan? Timeout)> GrantCalls { get; } = [];
        public string PeerId => "local-test";
        public string PeerName => "in-memory";
        public string PeerKind => "app";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public Exception? CloseReason => null;
        public ChannelReader<ChannelEvent> Events => events.Reader;
        public void Announce(string receiver)
        {
            Receiver = receiver;
            revision++;
            events.Writer.TryWrite(new("hello.changed", Interlocked.Increment(ref sequence), JsonSerializer.SerializeToElement(new { })));
        }
        public async Task<JsonElement> RequestAsync(string op, object? data = null, TimeSpan? timeout = null,
            CancellationToken ct = default, Action<JsonElement>? progress = null)
        {
            object result;
            if (op == Ops.Hello)
            { Hellos++; result = new HelloResult(1, new(Receiver, "in-memory", "windows", "test",
                ReadOnly: revision == 0 ? null : $"changed-{revision}"), []); }
            else
            {
                GrantCalls.Add((op, data, timeout));
                result = op switch
                {
                    "grant.binding" => await wire.BindAsync((FleetGrantBindingRequest)data!, ct),
                    "grant.read" when RawRead is { } raw => ParseRaw(raw),
                    "grant.read" => await wire.ReadProblemAsync((FleetGrantCall)data!, ct),
                    "grant.confirmIssue" => await wire.ConfirmIssueAsync((FleetGrantCall)data!, ct),
                    _ => throw new InvalidOperationException("Unexpected in-memory request")
                };
            }
            if (op == "grant.read" && ReadHeld is { } held) { await held.Task.WaitAsync(ct); }
            return JsonSerializer.SerializeToElement(new { ok = true, result }, ProtocolJson.Options);
        }
        public async Task<(JsonElement Response, long EventsBefore)> RequestOrderedAsync(string op, object? data = null,
            TimeSpan? timeout = null, CancellationToken ct = default, Action<JsonElement>? progress = null) =>
            (await RequestAsync(op, data, timeout, ct, progress), -1);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) =>
            Task.FromException<long>(new InvalidOperationException("Unexpected client event"));
        static JsonElement ParseRaw(string raw)
        { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
        public ValueTask DisposeAsync()
        { closed.TrySetResult(); events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
