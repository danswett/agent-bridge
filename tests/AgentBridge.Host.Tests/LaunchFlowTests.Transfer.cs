using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// A session that ended on one machine, resumed on another (docs/host-transfer.md): the phone
// signs a command for the machine the session is on, asks this machine (the test's host) to
// launch it, and this machine fetches the bundle that machine seals into the relay's blob store
// for it, installs it under an id of its own, and resumes it. Two real hosts through the
// in-process relay, each with its own agent homes.
public sealed partial class LaunchFlowTests
{
    const string AwayId = "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6";

    // The other machine: its own fixture homes, sources, launcher and transfers.
    sealed class Away(Fixture f, SessionSource source, Device device, SessionTransfers transfers, string events) : IAsyncDisposable
    {
        public Fixture F => f;
        public Device Device => device;
        public SessionTransfers Transfers => transfers;
        public string Events => events;

        public async ValueTask DisposeAsync()
        {
            await device.DisposeAsync();
            await source.DisposeAsync();
            transfers.Dispose();
            f.Dispose();
        }
    }

    async Task<Away> AwayAsync(bool serve = true, HttpClient? storage = null)
    {
        var fa = new Fixture();
        var appAway = Directory.CreateDirectory(Path.Combine(fa.Root, "repos", "app")).FullName;
        var dir = Directory.CreateDirectory(Path.Combine(fa.CopilotHome, "session-state", AwayId)).FullName;
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), $"id: {AwayId}\ncwd: '{appAway}'\nsummary: Fix the flaky test\n");
        var events = Path.Combine(dir, "events.jsonl");
        File.WriteAllText(events, $"{{\"type\":\"session.start\",\"data\":{{\"sessionId\":\"{AwayId}\"}}}}\n{{\"type\":\"user.message\",\"data\":{{\"content\":\"Fix the flaky test\"}}}}\n");
        var o = fa.Options();
        var awaySource = new SessionSource(new SessionSourceOptions
        {
            CopilotHome = o.CopilotHome,
            ClaudeHome = o.ClaudeHome,
            CodexHome = o.CodexHome,
            ClaudeRegistrationRoots = o.ClaudeRegistrationRoots,
            CodexRegistrationRoots = o.CodexRegistrationRoots,
            UseFileSystemWatcher = false,
            CoalesceInterval = TimeSpan.Zero,
        }, fa.Processes, TimeProvider.System);
        await awaySource.RefreshAsync();
        var device = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "Away host", "host");
        device.Hub.JsonOptions = ProtocolJson.Options;
        var transfers = new SessionTransfers(device.Key, Path.Combine(fa.Root, "profile", "transfers"), http: storage);
        var awayLocator = new FakeLocator();
        awayLocator.Installed["copilot"] = @"C:\fake\copilot.exe";
        var launcher = new LaunchService(awaySource, new FakeProbe(), new FakeScreens(), new FakeStarter(fa), awayLocator, new AgencyCatalog(new FakeRunner()), new LaunchServiceOptions
        {
            CopilotHome = fa.CopilotHome,
            ClaudeHome = fa.ClaudeHome,
            CodexHome = fa.CodexHome,
            Config = LaunchConfig.Default with { Workspaces = [new("App", appAway, false)], Transfer = TransferConfig.Default with { Serve = serve } },
            PlatformSupported = true,
            IsSystemDirectory = _ => false,
            Transfers = transfers,
            TransferStaging = transfers.Staging,
        });
        transfers.Launcher = launcher;
        transfers.Attach(device.Relay, device.Hub);
        device.Hub.Handler = new SessionService(awaySource, new MachineInfo(device.Id, "Away host", "windows", "0.1.0"), launcher: launcher, transfers: transfers).HandleAsync;
        return new Away(fa, awaySource, device, transfers, events);
    }

    // This machine, bringing sessions in.
    SessionTransfers ServeTransfers(Func<LaunchConfig, LaunchConfig>? change = null)
    {
        var transfers = new SessionTransfers(host.Key, Path.Combine(f.Root, "profile", "transfers"));
        var launcher = Launcher(Config(change), transfers: transfers);
        transfers.Launcher = launcher;
        transfers.Attach(host.Relay, host.Hub);
        host.Hub.Handler = new SessionService(source, new MachineInfo(host.Id, "Test host", "windows", "0.1.0"), launcher: launcher, transfers: transfers).HandleAsync;
        return transfers;
    }

    async Task<SecureChannel> OpenToAsync(Device machine)
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == machine.Id, Patience);
        return await client.Hub.OpenAsync(machine.Id);
    }

    // What the phone sends: the session, where it is, and a command it signed for that machine,
    // naming this one to receive it.
    object Transfer(Away away, SecureChannel toAway, string? to = null, string session = AwayId, P256KeyPair? signer = null)
    {
        var by = signer ?? client.Key;
        var now = DateTimeOffset.UtcNow;
        var command = SignedCommands.Sign(by, toAway.PeerStatic!, new CommandBody
        {
            V = 1,
            Action = TransferProtocol.Action,
            Request = Guid.NewGuid().ToString(),
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(by.Public) },
            Target = new CommandTarget { Machine = away.Device.Id, Session = session },
            To = to ?? host.Id,
            IssuedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(),
        });
        return new { from = away.Device.Id, session, command };
    }

    LocalBlobBackend RelayStore => (LocalBlobBackend)relay.App.Services.GetRequiredService<BlobService>().Backend!;

    static IEnumerable<string> Left(string folder) => Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder) : [];

    [Fact]
    public async Task A_session_that_ended_on_another_machine_is_brought_here_under_an_id_of_its_own_and_resumed_in_the_workspace_chosen()
    {
        await using var away = await AwayAsync();
        var transfers = ServeTransfers();
        var toAway = await OpenToAsync(away.Device);
        Assert.Contains(TransferProtocol.ServeCapability, await CapabilitiesAsync(toAway));
        var offered = await RequestAsync(toAway, LaunchProtocol.Options, new { });
        Assert.Contains(AwayId, offered.GetProperty("resumable").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        var ch = await OpenAsync();
        Assert.Contains(TransferProtocol.Capability, await CapabilitiesAsync(ch));
        var original = File.ReadAllText(away.Events);

        var o = await LaunchAsync(ch, new { agent = "copilot", workspace = "App", prompt = "carry on", transfer = Transfer(away, toAway) });

        Assert.Equal("started", o.Outcome_);
        Assert.NotNull(o.Session);
        Assert.NotEqual(AwayId, o.Session);
        var spec = Assert.Single(starter.Specs);
        Assert.Equal(app, spec.WorkingDirectory);
        Assert.Equal(o.Session, spec.Arguments[spec.Arguments.ToList().IndexOf("--session-id") + 1]);
        Assert.Equal("carry on", spec.Arguments[^1]);
        Assert.Equal(o.Session, o.Launched.GetProperty("resume").GetString());
        Assert.Equal((away.Device.Id, AwayId), (o.Launched.GetProperty("transfer").GetProperty("from").GetString(), o.Launched.GetProperty("transfer").GetProperty("session").GetString()));
        // The conversation, as its own session here.
        var installed = File.ReadAllText(Path.Combine(f.CopilotHome, "session-state", o.Session!, "events.jsonl"));
        Assert.Contains("Fix the flaky test", installed);
        Assert.Contains(o.Session!, installed);
        Assert.DoesNotContain(AwayId, installed);
        // The original stays exactly as it was, still resumable where it is.
        Assert.Equal(original, File.ReadAllText(away.Events));
        // Nothing is left in the blob store or either machine's staging.
        Assert.Empty(RelayStore.Stored);
        Assert.Empty(Left(transfers.Staging));
        Assert.Empty(Left(away.Transfers.Staging));
    }

    [Fact]
    public async Task A_command_is_carried_out_once_for_the_machines_it_names_and_by_a_machine_that_hands_its_sessions_on()
    {
        await using var away = await AwayAsync();
        ServeTransfers();
        var toAway = await OpenToAsync(away.Device);
        var ch = await OpenAsync();
        var transfer = Transfer(away, toAway);
        Assert.Equal("started", (await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer })).Outcome_);

        // The same command again, in another press: one press is one transfer.
        var again = await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer });
        Assert.Equal("refused", again.Outcome_);
        Assert.EndsWith("that command has been carried out already; press again", again.Reason);

        // Signed for another machine to receive it: this one will not carry it.
        var elsewhere = await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer = Transfer(away, toAway, to: client.Id) });
        Assert.Contains("the signed command is not for this launch", elsewhere.Reason);

        // Signed by another device than the one asking.
        using var stranger = P256KeyPair.Generate();
        Assert.Contains("the signed command is not for this launch", (await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer = Transfer(away, toAway, signer: stranger) })).Reason);

        // A session the other machine does not offer for resume cannot be asked for by name.
        var unknown = await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer = Transfer(away, toAway, session: Guid.NewGuid().ToString()) });
        Assert.EndsWith("that session is not one its machine offers (it is not on its resume list)", unknown.Reason);

        Assert.Single(starter.Specs);
        Assert.Empty(RelayStore.Stored);
    }

    // Storage that takes the connection and then never answers.
    sealed class StallingStorage : HttpMessageHandler
    {
        public int Uploads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Uploads);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_machine_whose_upload_stalls_gives_up_in_time_keeping_neither_the_bundle_nor_its_sending_slot()
    {
        var storage = new StallingStorage();
        await using var away = await AwayAsync(storage: new HttpClient(storage) { Timeout = Timeout.InfiniteTimeSpan });
        away.Transfers.SendFor = TimeSpan.FromSeconds(1);
        ServeTransfers();
        var toAway = await OpenToAsync(away.Device);
        var ch = await OpenAsync();

        // More than the two it sends at once: a slot held by a stalled upload would make the
        // third wait, and then say the machine is busy sending.
        for (var i = 0; i < 3; i++)
        {
            var o = await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer = Transfer(away, toAway) });
            Assert.Equal("refused", o.Outcome_);
            Assert.EndsWith("the bundle could not be uploaded in time; try again", o.Reason);
        }
        // Stalled in storage, not before it (a slow machine can spend the second packing).
        Assert.InRange(storage.Uploads, 1, 3);
        Assert.Empty(Left(away.Transfers.Staging));
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task Nothing_moves_from_a_machine_that_does_not_hand_its_sessions_on_or_to_one_that_does_not_take_them()
    {
        await using var away = await AwayAsync(serve: false);
        ServeTransfers();
        var toAway = await OpenToAsync(away.Device);
        Assert.DoesNotContain(TransferProtocol.ServeCapability, await CapabilitiesAsync(toAway));
        var ch = await OpenAsync();

        var o = await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer = Transfer(away, toAway) });
        Assert.Equal("refused", o.Outcome_);
        Assert.EndsWith("this machine does not hand its sessions to other machines (launch.transfer.serve in its config)", o.Reason);

        ServeTransfers(c => c with { Transfer = TransferConfig.Default with { Receive = false } });
        Assert.DoesNotContain(TransferProtocol.Capability, await CapabilitiesAsync(ch));
        Assert.Equal("bringing sessions from other machines is turned off here (launch.transfer.receive in the host's config)",
            (await LaunchAsync(ch, new { agent = "copilot", workspace = "App", transfer = Transfer(away, toAway) })).Reason);

        // And a transfer must say where it opens: the session's own folder is on the other machine.
        ServeTransfers();
        Assert.Equal("a session from another machine has no folder here; name the workspace it opens in",
            (await LaunchAsync(ch, new { agent = "copilot", transfer = Transfer(away, toAway) })).Reason);
        Assert.Empty(starter.Specs);
    }
}
