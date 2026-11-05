using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.SignIn;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class SignInTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string OtherId = "99999999-9999-4999-8999-999999999999";
    const string SyntheticCode = "TEST-0001";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, ProtocolJson.Options);
    static JsonElement Frame(FakeTimeProvider time, string? code = null, string? uri = null) =>
        Json(new { kind = "code", userCode = code ?? SyntheticCode, verificationUri = uri ?? "https://github.com/login/device", expiresAt = time.GetUtcNow().AddSeconds(10) });

    [Fact]
    public void Approved_native_command_disables_debug_logging_and_never_uses_a_shell_or_inherited_token()
    {
        using var fixture = new Fixture();
        var executable = Path.Combine(fixture.Root, "trusted-copilot");
        var start = CopilotDeviceCommand.Create(executable, fixture.CopilotHome);
        Assert.Equal(executable, start.FileName);
        Assert.Equal(["login", "--device-code", "--host", "https://github.com", "--log-level", "none"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal(fixture.CopilotHome, start.Environment["COPILOT_HOME"]);
        Assert.False(start.Environment.ContainsKey("COPILOT_GITHUB_TOKEN"));
        Assert.False(start.Environment.ContainsKey("GH_TOKEN"));
        Assert.False(start.Environment.ContainsKey("GITHUB_TOKEN"));
        Assert.Throws<ArgumentException>(() => CopilotDeviceCommand.Create("peer-supplied-command", fixture.CopilotHome));
    }

    [Theory]
    [InlineData("TEST-0001", "https://github.com/login/device", true)]
    [InlineData("test-0001", "https://github.com/login/device", false)]
    [InlineData("TEST–0001", "https://github.com/login/device", false)]
    [InlineData("TEST-000\n", "https://github.com/login/device", false)]
    [InlineData("TEST-\u202e001", "https://github.com/login/device", false)]
    [InlineData("TEST-0001", "https://github.com/login/device/", false)]
    [InlineData("TEST-0001", "https://github.com/login/device?next=evil", false)]
    [InlineData("TEST-0001", "https://github.com@evil.example/login/device", false)]
    [InlineData("TEST-0001", "https://GITHUB.COM/login/device", false)]
    [InlineData("TEST-0001", "http://github.com/login/device", false)]
    public async Task Private_frames_validate_exactly_without_echoing_rejected_input(string code, string uri, bool accepted)
    {
        var time = new FakeTimeProvider();
        var factory = new Factory();
        var runner = new PrivateSignInRunner(factory, time);
        var received = new List<PrivateSignInCode>();
        var running = runner.RunAsync(new("fixture-store", AgentKind.Copilot, null), (value, _) => { received.Add(value); return Task.FromResult(true); }, CancellationToken.None, () => true);
        await factory.Started.Task.WaitAsync(Patience);
        factory.Process.OutputPipe.Put(Frame(time, code, uri));
        factory.Process.Complete();
        Assert.Equal(accepted ? SignInOutcome.SignedIn : SignInOutcome.Refused, await running.WaitAsync(Patience));
        Assert.Equal(accepted ? 1 : 0, received.Count);
        Assert.All(received, value => Assert.DoesNotContain(SyntheticCode, value.ToString(), StringComparison.Ordinal));
        Assert.True(factory.Process.Disposed);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversize")]
    [InlineData("utf8")]
    [InlineData("duplicate-code")]
    [InlineData("policy")]
    [InlineData("persistence")]
    [InlineData("trailing")]
    [InlineData("ansi")]
    public async Task Malformed_private_output_fails_closed_and_aborts_the_helper(string variant)
    {
        var time = new FakeTimeProvider();
        var factory = new Factory();
        var runner = new PrivateSignInRunner(factory, time);
        var running = runner.RunAsync(new("store", AgentKind.Copilot, null), (_, _) => Task.FromResult(true), CancellationToken.None, () => true);
        await factory.Started.Task.WaitAsync(Patience);
        var p = factory.Process;
        if (variant == "duplicate") { p.OutputPipe.Put("{\"kind\":\"code\",\"kind\":\"code\"}\n"); }
        else if (variant == "unknown") { p.OutputPipe.Put("{\"kind\":\"code\",\"token\":\"must-not-be-read\"}\n"); }
        else if (variant == "oversize") { p.OutputPipe.Put(new string('x', 2049) + "\n"); }
        else if (variant == "utf8") { p.OutputPipe.Put([0xff, 10]); }
        else if (variant == "ansi") { p.OutputPipe.Put("\u001b[31m" + Frame(time).GetRawText() + "\n"); }
        else
        {
            p.OutputPipe.Put(Frame(time));
            if (variant == "duplicate-code") { p.OutputPipe.Put(Frame(time)); }
            else
            {
                p.OutputPipe.Put(Json(new { kind = "done", outcome = "signedIn", persisted = variant != "persistence", policyAllowed = variant != "policy" }));
                if (variant == "trailing") { p.OutputPipe.Put(Frame(time)); }
                p.Complete(done: false);
            }
        }
        Assert.Equal(SignInOutcome.Refused, await running.WaitAsync(Patience));
        Assert.True(p.Aborted);
        Assert.True(p.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fake_time_expires_code_or_initial_output_wait_and_aborts(bool issueCode)
    {
        var time = new FakeTimeProvider();
        var factory = new Factory();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PrivateSignInRunner(factory, time) { CodeWait = TimeSpan.FromSeconds(2) };
        var running = runner.RunAsync(new("store", AgentKind.Copilot, null), (_, _) => { shown.TrySetResult(); return Task.FromResult(true); }, CancellationToken.None, () => true);
        await factory.Started.Task.WaitAsync(Patience);
        if (issueCode)
        {
            factory.Process.OutputPipe.Put(Frame(time));
            await shown.Task.WaitAsync(Patience);
        }
        time.Advance(TimeSpan.FromSeconds(issueCode ? 11 : 3));
        Assert.Equal(SignInOutcome.Expired, await running.WaitAsync(Patience));
        Assert.True(factory.Process.Aborted);
    }

    [Fact]
    public async Task Nonzero_exit_and_private_stderr_are_not_reported_as_signed_in()
    {
        var factory = new Factory();
        var time = new FakeTimeProvider();
        var running = new PrivateSignInRunner(factory, time).RunAsync(new("store", AgentKind.Copilot, null), (_, _) => Task.FromResult(true), CancellationToken.None, () => true);
        await factory.Started.Task.WaitAsync(Patience);
        factory.Process.ErrorPipe.Put("synthetic private diagnostics never leave the runner");
        factory.Process.OutputPipe.Put(Frame(time));
        factory.Process.Complete(exit: 1);
        Assert.Equal(SignInOutcome.Failed, await running.WaitAsync(Patience));
        Assert.True(factory.Process.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_failure_or_oversized_private_stderr_fails_without_exposing_diagnostics(bool stderr)
    {
        var factory = new Factory { ThrowStart = !stderr };
        var running = new PrivateSignInRunner(factory, new FakeTimeProvider()).RunAsync(
            new("store", AgentKind.Copilot, null), (_, _) => Task.FromResult(true), CancellationToken.None, () => true);
        if (stderr)
        {
            await factory.Started.Task.WaitAsync(Patience);
            factory.Process.ErrorPipe.Put(new string('x', 32 * 1024 + 1));
        }
        Assert.Equal(SignInOutcome.Refused, await running.WaitAsync(Patience));
    }

    [Fact]
    public async Task Requester_only_delivery_dedup_join_and_same_key_reattach_are_explicit()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var observer = h.Phone("observer");
        var request = Guid.NewGuid();
        var first = h.Start(phone, request);
        Assert.Empty(phone.Events);
        Assert.Equal(0, h.Factory.Starts);
        var id = Json(first.Result).GetProperty("flow").GetString();
        await first.Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        Assert.Empty(observer.Events);
        Assert.Equal(1, h.Service.CodesHeld);
        Assert.DoesNotContain(SyntheticCode, Json(first.Result).GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticCode, Json(h.Source.Get(Id)!).GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(h.Logs, line => line.Contains(SyntheticCode, StringComparison.Ordinal));

        var duplicate = h.Start(phone, request);
        Assert.Equal(id, Json(duplicate.Result).GetProperty("flow").GetString());
        await duplicate.Then(phone);
        Assert.Equal(1, h.Factory.Starts);
        var join = h.Start(observer, Guid.NewGuid());
        Assert.Equal("busy", Json(join.Result).GetProperty("outcome").GetString());
        await join.Then(observer);
        await observer.Code.Task.WaitAsync(Patience);

        phone.Close();
        await h.Service.SweepAsync();
        var reattached = h.Phone();
        await h.Start(reattached, request).Then(reattached);
        await reattached.Code.Task.WaitAsync(Patience);
        Assert.Equal(1, h.Factory.Starts);
        h.Factory.Process.Complete();
        var done = await reattached.Done.Task.WaitAsync(Patience);
        Assert.Equal("signedIn", done.GetProperty("outcome").GetString());
        Assert.Equal(RecoveryState.Pending, h.Source.Get(Id)!.Attention!.Recovery);
        Assert.Equal(AttentionKind.SignInNeeded, h.Source.Get(Id)!.Attention!.Kind);
        Assert.Empty(done.GetProperty("resumed").EnumerateArray());
        Assert.Equal(0, h.Service.CodesHeld);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("role")]
    [InlineData("g5")]
    [InlineData("generation")]
    public async Task Authority_is_required_before_start_code_delivery_and_resume(string denial)
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        h.Authority.Denial = denial;
        if (denial == "g5") { h.ReadOnly = true; }
        Assert.Equal("refused", Json(h.Service.Start(phone, h.Request())).GetProperty("outcome").GetString());
        Assert.Equal(0, h.Factory.Starts);
        h.Authority.Denial = null;
        h.ReadOnly = false;
        var start = h.Start(phone);
        await start.Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Authority.Denial = denial;
        if (denial == "g5") { h.ReadOnly = true; }
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        h.Factory.Process.Complete();
        await h.Factory.Process.Disposing.Task.WaitAsync(Patience);
        await h.Service.SweepAsync();
        Assert.DoesNotContain(phone.Events, e => e.Name == AgentSignIn.Code);
        Assert.Equal(0, h.Recovery.Resumes);
        Assert.Equal(0, h.Service.CodesHeld);
    }

    [Fact]
    public async Task Revoke_after_code_purges_and_late_native_success_cannot_resume()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        await h.Start(phone).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        h.Authority.Denial = "revoked";
        await h.Service.SweepAsync();
        h.Factory.Process.Complete();
        await h.Factory.Process.Disposing.Task.WaitAsync(Patience);
        Assert.Equal(0, h.Service.CodesHeld);
        Assert.Equal(0, h.Recovery.Resumes);
        Assert.DoesNotContain(phone.Events, e => e.Name == AgentSignIn.Done);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authority_is_rechecked_after_source_state_wait_and_at_queued_factory_launch(bool queued)
    {
        await using var h = await Harness.CreateAsync();
        h.Factory.QueueStart = queued;
        if (!queued)
        {
            h.States.After = state =>
            {
                if (state == RecoveryState.SigningIn) { h.Authority.Denial = "revoked"; }
            };
        }
        var phone = h.Phone();
        await h.Start(phone).Then(phone);
        if (queued)
        {
            await h.Factory.Started.Task.WaitAsync(Patience);
            h.Authority.Denial = "revoked";
            h.Factory.ReleaseLaunch.TrySetResult();
            await h.Factory.Checked.Task.WaitAsync(Patience);
        }
        else { Assert.Equal(0, h.Factory.Starts); }
        await h.Service.SweepAsync();
        Assert.Equal(0, h.Factory.Launches);
        Assert.Equal(0, h.Recovery.Resumes);
        Assert.Equal(0, h.Service.CodesHeld);
    }

    [Fact]
    public async Task Revocation_is_detected_even_after_the_channel_closes_and_regrant_cannot_reattach_the_old_code()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        await h.Start(phone).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        phone.Close();
        h.Authority.Binding = "epoch:2";
        var replacement = h.Phone();
        Assert.Equal("refused", Json(h.Service.Start(replacement, h.Request())).GetProperty("outcome").GetString());
        await h.Service.SweepAsync();
        await h.Factory.Process.Disposing.Task.WaitAsync(Patience);
        Assert.Equal(0, h.Service.CodesHeld);
        Assert.Equal(0, h.Recovery.Resumes);
        Assert.Empty(replacement.Events);
    }

    [Fact]
    public async Task Terminal_receipt_replays_do_not_retain_reconnected_channels()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var request = Guid.NewGuid();
        await h.Start(phone, request).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        h.Factory.Process.Complete();
        await phone.Done.Task.WaitAsync(Patience);
        for (var i = 0; i < 20; i++)
        {
            var replay = h.Phone();
            await h.Start(replay, request).Then(replay);
            Assert.Equal(AgentSignIn.Done, Assert.Single(replay.Events).Name);
            replay.Close();
        }
        Assert.Equal(0, h.Service.ParticipantsHeld);
        Assert.Equal(1, h.Factory.Starts);
    }

    [Fact]
    public async Task A_late_factory_is_aborted_and_keeps_its_store_reserved_until_cleanup()
    {
        var time = new FakeTimeProvider();
        var factory = new Factory { DelayStart = true };
        var runner = new PrivateSignInRunner(factory, time) { Limit = TimeSpan.FromSeconds(2) };
        var target = new SignInTarget("store", AgentKind.Copilot, null);
        var running = runner.RunAsync(target, (_, _) => Task.FromResult(true), CancellationToken.None, () => true);
        await factory.Started.Task.WaitAsync(Patience);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(SignInOutcome.Expired, await running.WaitAsync(Patience));
        Assert.Equal(SignInOutcome.Refused, await runner.RunAsync(target, (_, _) => Task.FromResult(true), CancellationToken.None, () => true));
        Assert.Equal(1, factory.Starts);
        factory.Delayed.TrySetResult(factory.Process);
        await factory.Process.Disposing.Task.WaitAsync(Patience);
        Assert.True(factory.Process.Aborted);
    }

    [Fact]
    public async Task Eventual_successful_disposal_releases_quarantine_without_extending_the_callers_wait()
    {
        var time = new FakeTimeProvider();
        var factory = new Factory();
        factory.Process.HoldDisposal = true;
        var runner = new PrivateSignInRunner(factory, time) { Cleanup = TimeSpan.Zero };
        var target = new SignInTarget("store", AgentKind.Copilot, null);
        var running = runner.RunAsync(target, (_, _) => Task.FromResult(true), CancellationToken.None, () => true);
        await factory.Started.Task.WaitAsync(Patience);
        factory.Process.OutputPipe.Put(Frame(time));
        factory.Process.Complete();
        Assert.Equal(SignInOutcome.Failed, await running.WaitAsync(Patience));
        Assert.Equal(SignInOutcome.Refused, await runner.RunAsync(target, (_, _) => Task.FromResult(true), CancellationToken.None, () => true));
        factory.Process.ReleaseDisposal.TrySetResult();
        Assert.NotEqual(SignInOutcome.Refused, await runner.RunAsync(target, (_, _) => Task.FromResult(true), CancellationToken.None, () => true));
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task Cancellation_does_not_wait_forever_for_a_terminal_source_overlay()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var start = h.Start(phone);
        var flow = Json(start.Result).GetProperty("flow").GetString();
        await start.Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        h.States.HangTerminal = true;
        var cancelling = h.Service.CancelAsync(phone, Json(new { flow }));
        await h.States.TerminalWaiting.Task.WaitAsync(Patience);
        Assert.Equal(0, h.Service.CodesHeld);
        await h.Factory.Process.Disposing.Task.WaitAsync(Patience);
        h.Time.Advance(h.Service.CheckLimit + TimeSpan.FromSeconds(1));
        Assert.Equal("cancelled", Json(await cancelling.WaitAsync(Patience)).GetProperty("outcome").GetString());
        Assert.Equal("cancelled", (await phone.Done.Task.WaitAsync(Patience)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("shutdown")]
    [InlineData("disconnect")]
    public async Task Terminal_paths_purge_private_code_and_stop_the_process(string terminal)
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var start = h.Start(phone);
        var flow = Json(start.Result).GetProperty("flow").GetString();
        await start.Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        if (terminal == "cancel")
        {
            var stranger = h.Phone("stranger");
            Assert.Equal("refused", Json(await h.Service.CancelAsync(stranger, Json(new { flow }))).GetProperty("outcome").GetString());
            await h.Service.CancelAsync(phone, Json(new { flow }));
        }
        else if (terminal == "shutdown") { await h.Service.DisposeAsync(); }
        else
        {
            if (terminal == "disconnect") { phone.Close(); await h.Service.SweepAsync(); }
            h.Time.Advance(TimeSpan.FromSeconds(31));
            await h.Service.SweepAsync();
        }
        await h.Factory.Process.Disposing.Task.WaitAsync(Patience);
        Assert.Equal(0, h.Service.CodesHeld);
        Assert.True(h.Factory.Process.Aborted);
        Assert.Equal(0, h.Recovery.Resumes);
    }

    [Fact]
    public async Task A_store_is_serialized_and_conflicting_uuid_reuse_is_refused()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var request = Guid.NewGuid();
        await h.Start(phone, request).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Resolver.Store = "other-store";
        Assert.Equal("refused", Json(h.Service.Start(phone, h.Request(request))).GetProperty("outcome").GetString());
        h.Resolver.Store = "fixture-store";
        h.Resolver.Account = "another";
        Assert.Equal("refused", Json(h.Service.Start(phone, h.Request())).GetProperty("outcome").GetString());
        Assert.Equal(1, h.Factory.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Silent_candidate_stays_pending_without_claiming_recovery_until_real_model_output(bool automatic)
    {
        await using var h = await Harness.CreateAsync();
        h.Recovery.ModelEvidence = true;
        var phone = h.Phone();
        if (automatic) { Assert.True(await h.Service.TryPrepareSilentRecoveryAsync(AgentKind.Copilot, null)); }
        else { await h.Start(phone).Then(phone); }
        Assert.Equal(0, h.Factory.Starts);
        Assert.Equal(RecoveryState.Pending, h.Source.Get(Id)!.Attention!.Recovery);
        Assert.Empty(phone.Events);
        Fixture.Append(h.Events, Copilot.Event("user.message", new { content = "retry" }, 3), Copilot.TurnStart(4), Copilot.TurnEnd(5));
        await h.Source.RefreshAsync();
        await h.Service.SweepAsync();
        Assert.Equal(AttentionKind.SignInNeeded, h.Source.Get(Id)!.Attention!.Kind);
        Assert.Empty(phone.Events);
        Fixture.Append(h.Events, Copilot.Assistant("real model output", 6));
        await h.Source.RefreshAsync();
        await h.Service.SweepAsync();
        Assert.Null(h.Source.Get(Id)!.Attention);
        if (automatic) { Assert.Empty(phone.Events); }
        else { Assert.Equal("signedIn", (await phone.Done.Task.WaitAsync(Patience)).GetProperty("outcome").GetString()); }
    }

    [Fact]
    public async Task Each_session_in_a_silent_store_flow_waits_for_its_own_model_proof()
    {
        await using var h = await Harness.CreateAsync();
        var other = h.Fixture.CopilotSession(OtherId, 101);
        h.Fixture.Processes.Run(AgentKind.Copilot, 101);
        Fixture.Append(other, AuthError(2));
        await h.Source.RefreshAsync();
        h.Resolver.Ids = [Id, OtherId];
        h.Recovery.ModelEvidence = true;
        h.Recovery.Result = new([Id], [OtherId]);
        var phone = h.Phone();
        var request = Guid.NewGuid();
        await h.Start(phone, request).Then(phone);
        Fixture.Append(h.Events, Copilot.Assistant("first model reply", 3));
        await h.Source.RefreshAsync();
        await h.Service.SweepAsync();
        Assert.Null(h.Source.Get(Id)!.Attention);
        Assert.Equal(RecoveryState.Pending, h.Source.Get(OtherId)!.Attention!.Recovery);
        Assert.Empty(phone.Events);
        phone.Close();
        var reattached = h.Phone();
        await h.Start(reattached, request).Then(reattached);
        Fixture.Append(other, Copilot.Assistant("second model reply", 4));
        await h.Source.RefreshAsync();
        await h.Service.SweepAsync();
        Assert.Null(h.Source.Get(OtherId)!.Attention);
        var done = await reattached.Done.Task.WaitAsync(Patience);
        Assert.Equal("signedIn", done.GetProperty("outcome").GetString());
        Assert.Equal(Id, Assert.Single(done.GetProperty("resumed").EnumerateArray()).GetString());
        Assert.Equal(OtherId, Assert.Single(done.GetProperty("continue").EnumerateArray()).GetString());
        var receipt = h.Phone();
        await h.Start(receipt, request).Then(receipt);
        Assert.Equal(done.GetRawText(), (await receipt.Done.Task.WaitAsync(Patience)).GetRawText());
    }

    static string AuthError(int second) => Copilot.Event("session.error", new { errorType = "authorization",
        message = "Authorization error. Your credentials may be expired or invalid. [request-id redacted]",
        statusCode = 401, remediation = "sign_in" }, second);

    [Fact]
    public async Task Framework_is_not_advertised_or_callable_in_default_service_and_plain_channels_cannot_prove_personhood()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var service = new SessionService(h.Source, new MachineInfo("host", "Host", "windows", "1.0")) { SignIn = h.Service };
        var hello = (HelloResult)(await service.HandleAsync(phone, Ops.Hello, Json(new { })))!;
        Assert.DoesNotContain(AgentSignIn.Capability, hello.Capabilities);
        var result = await service.HandleAsync(phone, AgentSignIn.Start, h.Request());
        Assert.Equal("refused", Json(result!).GetProperty("outcome").GetString());
        using var key = P256KeyPair.Generate();
        var store = new TrustedPeerStore(Path.Combine(h.Fixture.Root, "trust"), key.Public, () => "account", h.Time);
        var authority = new PersonSignInAuthority(store, () => false, _ => true, _ => true);
        Assert.Null(authority.Current(phone));
        Assert.Equal(0, h.Factory.Starts);
    }

    sealed class Harness : IAsyncDisposable
    {
        public Fixture Fixture { get; } = new();
        public FakeTimeProvider Time => Fixture.Time;
        public string Events = "";
        public SessionSource Source = null!;
        public StateSource States = null!;
        public Factory Factory { get; } = new();
        public List<string> Logs { get; } = [];
        public Authority Authority { get; } = new();
        public Resolver Resolver = null!;
        public Recovery Recovery { get; } = new();
        public AgentSignInService Service = null!;
        public bool ReadOnly;
        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h.Events = h.Fixture.CopilotSession(Id, 100);
            h.Fixture.Processes.Run(AgentKind.Copilot, 100);
            Fixture.Append(h.Events, AuthError(2));
            h.Source = h.Fixture.Source(log: h.Logs.Add);
            await h.Source.RefreshAsync();
            Assert.Equal(AttentionKind.SignInNeeded, h.Source.Get(Id)!.Attention!.Kind);
            h.Resolver = new(h.Source);
            h.States = new(h.Source);
            h.Service = new(h.States, h.Authority, h.Resolver, h.Recovery, new(h.Factory, h.Time), () => h.ReadOnly, h.Time) { Enabled = true };
            return h;
        }
        public Phone Phone(string device = "phone") => new(device);
        public JsonElement Request(Guid? request = null) => Json(new { request = request ?? Guid.NewGuid(), agent = "copilot" });
        public ChannelReply Start(Phone phone, Guid? request = null) => Assert.IsType<ChannelReply>(Service.Start(phone, Request(request)));
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Source.DisposeAsync(); Fixture.Dispose(); }
    }

    sealed class Authority : ISignInAuthority
    {
        public string? Denial;
        public string Binding = "epoch:1";
        public SignInGrant? Current(IProtocolChannel channel) => Denial is null && channel is Phone p ? new(p.PeerId, Binding) : null;
        public bool StillCurrent(SignInGrant grant) => Denial is null && grant.Binding == Binding;
    }
    sealed class StateSource(ISessionSource inner) : ISessionSource
    {
        public Action<RecoveryState>? After;
        public bool HangTerminal;
        public TaskCompletionSource TerminalWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Revision => inner.Revision;
        public event EventHandler<SessionChange>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public IReadOnlyList<Session> Snapshot() => inner.Snapshot();
        public SessionList List(string? cursor = null) => inner.List(cursor);
        public Session? Get(string id) => inner.Get(id);
        public SessionSubscription Subscribe() => inner.Subscribe();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => inner.RefreshAsync(cancellationToken);
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async Task SetSignInRecoveryAsync(string id, DateTimeOffset errorAt, string generation, RecoveryState state,
            bool begin = false, CancellationToken cancellationToken = default)
        {
            if (HangTerminal && state == RecoveryState.Failed) { TerminalWaiting.TrySetResult(); await stalled.Task; }
            await inner.SetSignInRecoveryAsync(id, errorAt, generation, state, begin, cancellationToken);
            After?.Invoke(state);
        }
    }
    sealed class Resolver(SessionSource source) : ISignInContextResolver
    {
        public string Store = "fixture-store";
        public string? Account;
        public string[] Ids = [Id];
        public SignInTarget? ResolveTarget(AgentKind agent, string? account) => new(Store, agent, Account);
        public SignInContext? Resolve(AgentKind agent, string? account) => source.Get(Id)?.Attention is not null
            ? new(new(Store, agent, Account), Ids.Select(id => new SignInFailure(id, source.Get(id)!.Attention!.Since)).ToArray()) : null;
    }
    sealed class Recovery : ISignInRecovery
    {
        public bool ModelEvidence;
        public int Resumes;
        public SignInResume Result = new([], []);
        public Task<bool> HasModelEvidenceAsync(SignInTarget target, CancellationToken ct) => Task.FromResult(ModelEvidence);
        public Task<SignInResume> ResumeAsync(SignInContext context, Func<bool> authorize, CancellationToken ct)
        {
            Assert.True(authorize());
            ++Resumes;
            return Task.FromResult(Result);
        }
    }
    sealed class Phone(string id) : IProtocolChannel
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PeerId => id;
        public string PeerName => "Fixture person";
        public string PeerKind => "app";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public List<(string Name, JsonElement Value)> Events { get; } = [];
        public TaskCompletionSource<JsonElement> Code { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
        {
            var value = Json(data!);
            lock (Events) { Events.Add((name, value)); }
            if (name == AgentSignIn.Code) { Code.TrySetResult(value); }
            if (name == AgentSignIn.Done) { Done.TrySetResult(value); }
            return Task.FromResult(1L);
        }
        public void Close() => closed.TrySetResult();
    }
    sealed class Factory : IPrivateSignInProcessFactory
    {
        public bool DelayStart;
        public bool ThrowStart;
        public bool QueueStart;
        public int Launches;
        public TaskCompletionSource ReleaseLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IPrivateSignInProcess> Delayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process Process { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public Task<IPrivateSignInProcess> StartAsync(SignInTarget target, Func<bool> authorize, CancellationToken ct)
        {
            ++Starts; Started.TrySetResult();
            if (ThrowStart) { throw new IOException("synthetic private diagnostics"); }
            if (DelayStart) { return Delayed.Task; }
            return LaunchAsync();
            async Task<IPrivateSignInProcess> LaunchAsync()
            {
                if (QueueStart) { await ReleaseLaunch.Task.WaitAsync(ct); }
                Checked.TrySetResult();
                if (!authorize()) { throw new InvalidOperationException("sign-in launch is no longer authorized"); }
                ct.ThrowIfCancellationRequested();
                ++Launches;
                return Process;
            }
        }
    }
    sealed class Process : IPrivateSignInProcess
    {
        public Pipe OutputPipe { get; } = new();
        public Pipe ErrorPipe { get; } = new();
        readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream Output => OutputPipe;
        public Stream Error => ErrorPipe;
        public Task<int> ExitCode => exit.Task;
        public bool Aborted;
        public bool Disposed;
        public bool HoldDisposal;
        public TaskCompletionSource ReleaseDisposal { get; } = new();
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(int exit = 0, bool done = true)
        {
            if (done) { OutputPipe.Put(Json(new { kind = "done", outcome = "signedIn", persisted = true, policyAllowed = true })); }
            OutputPipe.End(); ErrorPipe.End(); this.exit.TrySetResult(exit);
        }
        public void Abort() { Aborted = true; OutputPipe.End(); ErrorPipe.End(); exit.TrySetResult(1); }
        public ValueTask DisposeAsync()
        {
            Disposed = true; Disposing.TrySetResult();
            return HoldDisposal ? new ValueTask(ReleaseDisposal.Task) : ValueTask.CompletedTask;
        }
    }
    sealed class Pipe : Stream
    {
        readonly Channel<byte[]> queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        byte[] current = [];
        int at;
        public void Put(JsonElement value) => Put(value.GetRawText() + "\n");
        public void Put(string value) => Put(Encoding.UTF8.GetBytes(value));
        public void Put(byte[] value) => queue.Writer.TryWrite(value);
        public void End() => queue.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (at == current.Length)
            {
                if (!await queue.Reader.WaitToReadAsync(cancellationToken)) { return 0; }
                current = await queue.Reader.ReadAsync(cancellationToken); at = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - at);
            current.AsMemory(at, count).CopyTo(buffer); at += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
