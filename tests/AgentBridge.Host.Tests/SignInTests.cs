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

    [Theory]
    [InlineData("account", false)]
    [InlineData("signIn", false)]
    [InlineData("recovery", false)]
    [InlineData("account", true)]
    [InlineData("signIn", true)]
    [InlineData("recovery", true)]
    public async Task Agent_output_cannot_supply_account_sign_in_or_recovery_metadata(string field, bool onDone)
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        await h.Start(phone).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        if (onDone)
        {
            h.Factory.Process.OutputPipe.Put(Frame(h.Time));
            await phone.Code.Task.WaitAsync(Patience);
        }
        var value = field switch { "signIn" => "phone", "recovery" => "pending", _ => "synthetic-agent-account-text" };
        var frame = onDone ? Json(new { kind = "done", outcome = "signedIn", persisted = true, policyAllowed = true }) : Frame(h.Time);
        var fields = frame.Deserialize<Dictionary<string, JsonElement>>()!;
        fields[field] = Json(value);
        h.Factory.Process.OutputPipe.Put(Json(fields));
        h.Factory.Process.ErrorPipe.Put("synthetic agent prose: account=synthetic-agent-account-text; signIn=phone; recovery=pending");
        h.Factory.Process.Complete(done: false);
        Assert.Equal("refused", (await phone.Done.Task.WaitAsync(Patience)).GetProperty("outcome").GetString());
        var attention = h.Source.Get(Id)!.Attention!;
        Assert.Null(attention.Account);
        Assert.Null(attention.SignIn);
        Assert.Equal(RecoveryState.Failed, attention.Recovery);
        Assert.DoesNotContain("synthetic-agent-account-text", Json(h.Source.Get(Id)!).GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(phone.Events, e => e.Value.GetRawText().Contains("synthetic-agent-account-text", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, line => line.Contains("synthetic-agent-account-text", StringComparison.Ordinal));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_delivery_and_receipt_replay_cancel_the_underlying_send_on_deadline(bool replay)
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var request = Guid.NewGuid();
        await h.Start(phone, request).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        Task? replaying = null;
        try
        {
            phone.BlockSends = !replay;
            h.Factory.Process.Complete();
            if (replay)
            {
                await phone.Done.Task.WaitAsync(Patience);
                phone.BlockSends = true;
                replaying = h.Start(phone, request).Then(phone);
            }
            await phone.SendBlocked.Task.WaitAsync(Patience);
            h.Time.Advance(h.Service.CheckLimit + TimeSpan.FromSeconds(1));
            await phone.SendFinished.Task.WaitAsync(Patience);
            if (replaying is not null) { await replaying.WaitAsync(Patience); }
            Assert.True(phone.HeldToken.IsCancellationRequested);
            Assert.Equal(0, phone.PendingSends);
        }
        finally { phone.ReleaseSends.TrySetResult(); }
    }

    [Fact]
    public async Task An_inflight_uuid_with_a_newer_auth_failure_is_refused_instead_of_silently_busy()
    {
        await using var h = await Harness.CreateAsync();
        var phone = h.Phone();
        var request = Guid.NewGuid();
        await h.Start(phone, request).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);
        Fixture.Append(h.Events, AuthError(3));
        await h.Source.RefreshAsync();
        var replacement = h.Phone();
        Assert.Equal("refused", Json(h.Service.Start(replacement, h.Request(request))).GetProperty("outcome").GetString());
        Assert.Empty(replacement.Events);
        Assert.Equal(1, h.Factory.Starts);
        await h.Service.SweepAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifetime_expiry_is_expired_when_the_worker_finishes_before_the_sweep(bool evidence)
    {
        await using var h = await Harness.CreateAsync(TimeSpan.FromSeconds(2), manualSweeps: true);
        h.Factory.DelayStart = !evidence;
        h.Recovery.ModelEvidence = evidence;
        h.Recovery.HoldResume = evidence;
        var phone = h.Phone();
        await h.Start(phone).Then(phone);
        if (evidence) { await h.Recovery.ResumeEntered.Task.WaitAsync(Patience); }
        else { await h.Factory.Started.Task.WaitAsync(Patience); }
        try
        {
            h.Time.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal("expired", (await phone.Done.Task.WaitAsync(Patience)).GetProperty("outcome").GetString());
        }
        finally
        {
            h.Factory.Delayed.TrySetResult(h.Factory.Process);
            h.Recovery.ReleaseResume.TrySetResult(new([], []));
        }
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
        var authority = new RosterSignInAuthority(new NoPersonRoster(), () => false, _ => true);
        Assert.Null(authority.Current(phone));
        Assert.Equal(0, h.Factory.Starts);
    }

    // #972: the machine's own permissions.phoneSignIn, read as serve reads config.json, is the only
    // switch; and an agent needs an adapter that can run here as well (Windows, Copilot's .exe found).
    [Theory]
    [InlineData("{}", true, false)]
    [InlineData("{\"permissions\":{\"phoneSignIn\":false}}", true, false)]
    [InlineData("{\"permissions\":{\"phoneSignIn\":\"true\"}}", true, false)]
    [InlineData("{\"permissions\":{\"phoneSignIn\":true}}", false, false)]
    [InlineData("{\"permissions\":{\"phoneSignIn\":true}}", true, true)]
    public async Task Sign_in_is_offered_and_started_only_where_the_machine_turns_it_on_and_an_adapter_exists(string config, bool adapter, bool offered)
    {
        var (settings, _) = HostSettings.Parse(config, HostSettings.FileName);
        await using var h = await Harness.CreateAsync(phoneSignIn: PhoneSignInSetup.Agents(settings, adapter));
        await using var signIn = PhoneSignInSetup.Create(settings, adapter, h.States, h.Authority, h.Resolver, h.Factory, () => false, h.Time);
        var service = new SessionService(h.Source, new MachineInfo("host", "Host", "windows", "1.0")) { SignIn = signIn, SignInEntrypointApproved = signIn is not null };
        var phone = h.Phone();

        var hello = (HelloResult)(await service.HandleAsync(phone, Ops.Hello, Json(new { })))!;
        var result = await service.HandleAsync(phone, AgentSignIn.Start, h.Request());

        Assert.Equal(offered, hello.Capabilities.Contains(AgentSignIn.Capability));
        Assert.Equal(offered ? "started" : "refused", Json(result is ChannelReply reply ? reply.Result : result!).GetProperty("outcome").GetString());
        Assert.Equal(offered ? SignInWhere.Phone : null, h.Source.Get(Id)!.Attention!.SignIn);
        if (result is ChannelReply started) { await started.Then(phone); await h.Factory.Started.Task.WaitAsync(Patience); }
        Assert.Equal(offered ? 1 : 0, h.Factory.Starts);
    }

    // #972 slice 2: a machine with the switch on and Copilot's adapter, but nobody the roster pins,
    // says nothing a device could take for a Sign in; it says phone once someone is pinned.
    [Fact]
    public async Task With_nobody_pinned_no_attention_says_phone_and_once_someone_is_it_does()
    {
        await using var h = await Harness.CreateAsync(phoneSignIn: [AgentKind.Copilot]);
        h.Pinned = false;
        await h.Source.RefreshAsync();
        Assert.Null(h.Source.Get(Id)!.Attention!.SignIn);

        h.Pinned = true;
        await h.Source.RefreshAsync();
        Assert.Equal(SignInWhere.Phone, h.Source.Get(Id)!.Attention!.SignIn);
    }

    // Trust's condition 4: a pin that goes ends the flow when the authority says so, not at the next
    // tick (here there is none: the sweep timer never fires).
    [Fact]
    public async Task A_pin_that_goes_ends_the_flow_as_soon_as_the_authority_says_it_changed()
    {
        await using var h = await Harness.CreateAsync(manualSweeps: true);
        var phone = h.Phone();
        await h.Start(phone).Then(phone);
        await h.Factory.Started.Task.WaitAsync(Patience);
        h.Factory.Process.OutputPipe.Put(Frame(h.Time));
        await phone.Code.Task.WaitAsync(Patience);

        h.Authority.Denial = "removed from the roster";
        h.Authority.Change();

        await h.Factory.Process.Disposing.Task.WaitAsync(Patience);
        Assert.Equal(0, h.Service.CodesHeld);
    }

    sealed class Roster : IPersonDeviceRoster
    {
        public RosterState State { get; set; } = RosterState.Ready;
        public List<PinnedDevice> Devices { get; } = [];
        public bool Throws;
        public PinnedDevice? Find(byte[] key, string account) => null;
        public IReadOnlyList<PinnedDevice> Pinned(string account) => Throws ? throw new IOException("unreadable") : Devices;
        public bool StillPinned(PinnedDevice device) => Devices.Contains(device);
        public event Action? Changed { add { } remove { } }
    }

    static PinnedDevice Device(string name, string kind = "phone", string presence = "key", string id = "0123456789abcdef0123456789abcdef") =>
        new(id, kind, "iOS", name, presence, "7", "add-hash");

    // What the app's Settings and a device's Machines page list: only devices the roster pins as a
    // person's with a presence-proven key, by name, kind and platform; nobody unless it is read.
    [Theory]
    [InlineData(RosterState.None, false, "none")]
    [InlineData(RosterState.Unreadable, false, "unreadable")]
    [InlineData(RosterState.Ready, true, "unreadable")]
    [InlineData(RosterState.Ready, false, "ready")]
    public void Who_can_sign_in_lists_only_pinned_person_devices_and_nobody_on_doubt(RosterState state, bool throws, string said)
    {
        var roster = new Roster { State = state, Throws = throws };
        roster.Devices.AddRange([Device("iPhone"), Device("Work laptop", "app", "webauthn", "fedcba9876543210fedcba9876543210"),
            Device("Unproven", "pwa", "none"), Device("Another host", "host")]);

        var view = PersonDevices.View(roster, "account", "fedcba9876543210fedcba9876543210");

        Assert.Equal(said, view.State);
        if (state == RosterState.Ready && !throws)
        {
            Assert.Equal([new SignInDevice("iPhone", "phone", "iOS"), new SignInDevice("Work laptop", "app", "iOS", true)], view.Devices);
            var json = Json(view).GetRawText();
            foreach (var secret in new[] { "0123456789abcdef", "fedcba9876543210", "add-hash", "webauthn", "\"7\"" }) { Assert.DoesNotContain(secret, json); }
        }
        else { Assert.Empty(view.Devices); }
        Assert.Empty(PersonDevices.View(roster, null, null).Devices);
    }
    const string SyntheticToken = "gho_SYNTHETICTOKEN000000000000000000000";
    const string SyntheticLogin = "synthetic-login";

    static string CliCode(string code = SyntheticCode, string uri = "https://github.com/login/device") =>
        $"To authenticate, visit {uri} and enter code {code}\n";

    // The production adapter's process, over pipes this test writes as Copilot 1.0.105's login would.
    sealed class CliFactory(TimeProvider time) : IPrivateSignInProcessFactory
    {
        public Pipe Out { get; } = new();
        public Pipe Err { get; } = new();
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Aborted;
        public Task<IPrivateSignInProcess> StartAsync(SignInTarget target, Func<bool> authorize, CancellationToken ct)
        {
            Started.TrySetResult();
            return Task.FromResult<IPrivateSignInProcess>(new CopilotLoginProcess(Out, Err, Exit.Task,
                () => { Aborted = true; Out.End(); Err.End(); Exit.TrySetResult(1); }, () => ValueTask.CompletedTask, time));
        }
        public void Finish(int exit) { Out.End(); Err.End(); Exit.TrySetResult(exit); }
    }

    [Fact]
    public async Task Copilot_login_output_reaches_only_the_requesting_device_and_only_as_signin_code()
    {
        var on = HostSettings.Parse("{\"permissions\":{\"phoneSignIn\":true}}", HostSettings.FileName).Settings;
        await using var h = await Harness.CreateAsync(phoneSignIn: [AgentKind.Copilot]);
        var cli = new CliFactory(h.Time);
        await using var service = PhoneSignInSetup.Create(on, copilotAdapter: true, h.States, h.Authority, h.Resolver, cli, () => false, h.Time)!;
        var phone = h.Phone();
        var observer = h.Phone("observer");
        await Assert.IsType<ChannelReply>(service.Start(phone, h.Request())).Then(phone);
        await cli.Started.Task.WaitAsync(Patience);

        cli.Out.Put(CliCode() + "Waiting for authorization...\n");
        cli.Err.Put($"Failed to open browser. Please visit https://github.com/login/device and enter the code {SyntheticCode} manually.\nGH_TOKEN={SyntheticToken}\n");
        var code = await phone.Code.Task.WaitAsync(Patience);
        cli.Out.Put($"Signed in successfully as {SyntheticLogin} (GitHub.com).\n");
        cli.Finish(0);
        var done = await phone.Done.Task.WaitAsync(Patience);

        Assert.Equal(["flow", "agent", "userCode", "verificationUri", "expiresAt"], code.EnumerateObject().Select(p => p.Name));
        Assert.Equal(SyntheticCode, code.GetProperty("userCode").GetString());
        Assert.Equal("https://github.com/login/device", code.GetProperty("verificationUri").GetString());
        Assert.Equal("signedIn", done.GetProperty("outcome").GetString());
        Assert.Empty(observer.Events);
        var everywhere = phone.Events.Where(e => e.Name != AgentSignIn.Code).Select(e => e.Value.GetRawText())
            .Concat(h.Logs).Append(Json(h.Source.Get(Id)!).GetRawText()).Append(code.GetRawText()).ToList();
        foreach (var secret in new[] { SyntheticToken, SyntheticLogin })
        {
            Assert.DoesNotContain(everywhere, text => text.Contains(secret, StringComparison.Ordinal));
        }
        Assert.DoesNotContain(everywhere.Take(everywhere.Count - 1), text => text.Contains(SyntheticCode, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("order")]
    [InlineData("unknown")]
    [InlineData("lowercase")]
    [InlineData("near-url")]
    [InlineData("ansi")]
    [InlineData("trailing")]
    [InlineData("exit")]
    public async Task Copilot_login_output_the_adapter_does_not_know_fails_closed_without_echoing_it(string variant)
    {
        var time = new FakeTimeProvider();
        var cli = new CliFactory(time);
        var shown = new List<PrivateSignInCode>();
        var running = new PrivateSignInRunner(cli, time).RunAsync(new("store", AgentKind.Copilot, null),
            (c, _) => { shown.Add(c); return Task.FromResult(true); }, CancellationToken.None, () => true);
        await cli.Started.Task.WaitAsync(Patience);
        var success = $"Signed in successfully as {SyntheticLogin}.\n";
        cli.Out.Put(variant switch
        {
            "order" => "Waiting for authorization...\n" + CliCode() + success,
            "unknown" => "Paste your authentication token: " + SyntheticToken + "\n",
            "lowercase" => CliCode("test-0001") + "Waiting for authorization...\n" + success,
            "near-url" => CliCode(uri: "https://github.com/login/device?next=" + SyntheticToken) + "Waiting for authorization...\n" + success,
            "ansi" => "\u001b[1m" + CliCode() + "Waiting for authorization...\n" + success,
            "trailing" => CliCode() + "Waiting for authorization...\n" + success + success,
            _ => CliCode() + "Waiting for authorization...\n" + success,
        });
        cli.Finish(variant == "exit" ? 1 : 0);

        Assert.Equal(variant == "exit" ? SignInOutcome.Failed : SignInOutcome.Refused, await running.WaitAsync(Patience));
        Assert.True(cli.Aborted);
        Assert.Equal(variant is "trailing" or "exit" ? 1 : 0, shown.Count);
    }

    [Fact]
    public async Task The_production_adapter_starts_copilots_own_login_with_debug_logging_off_and_never_through_a_shell()
    {
        using var fixture = new Fixture();
        var home = Path.GetFullPath(fixture.CopilotHome);
        var exe = Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        IPrivateSignInProcessFactory Adapter(string path) => new CopilotSignInAdapter(() => path, fixture.CopilotHome, fixture.Time, info => { started.Add(info); return null; });
        var target = new SignInTarget(home, AgentKind.Copilot, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(exe).StartAsync(target, () => false, CancellationToken.None));
        Assert.Empty(started);
        // The CLI's own debug override, inherited from wherever the host was started.
        Environment.SetEnvironmentVariable("COPILOT_DEBUG_BROWSER", "[\"synthetic-browser\"]");
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(exe).StartAsync(target, () => true, CancellationToken.None)); }
        finally { Environment.SetEnvironmentVariable("COPILOT_DEBUG_BROWSER", null); }
        var info = Assert.Single(started);        Assert.Equal(exe, info.FileName);
        Assert.Equal(["login", "--device-code", "--host", "https://github.com", "--log-level", "none"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal(home, info.Environment["COPILOT_HOME"]);
        Assert.DoesNotContain(info.Environment.Keys, k => k.StartsWith("COPILOT_DEBUG", StringComparison.OrdinalIgnoreCase));

        // Refused before anything starts: another store, or a shim needing a shell.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(exe).StartAsync(target with { Store = fixture.Root }, () => true, CancellationToken.None));
        if (OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(Path.Combine(fixture.Root, "copilot.cmd")).StartAsync(target, () => true, CancellationToken.None));
        }
        Assert.Single(started);
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
        // Whether the roster pins anyone (SessionSourceOptions.PhoneSignInPinned).
        public bool Pinned = true;
        public static async Task<Harness> CreateAsync(TimeSpan? limit = null, bool manualSweeps = false, IReadOnlyCollection<AgentKind>? phoneSignIn = null)
        {
            var h = new Harness();
            h.Events = h.Fixture.CopilotSession(Id, 100);
            h.Fixture.Processes.Run(AgentKind.Copilot, 100);
            Fixture.Append(h.Events, AuthError(2));
            h.Source = h.Fixture.Source(log: h.Logs.Add, phoneSignIn: phoneSignIn, phoneSignInPinned: () => h.Pinned);
            await h.Source.RefreshAsync();
            Assert.Equal(AttentionKind.SignInNeeded, h.Source.Get(Id)!.Attention!.Kind);
            h.Resolver = new(h.Source);
            h.States = new(h.Source);
            TimeProvider clock = manualSweeps ? new ManualSweepClock(h.Time) : h.Time;
            h.Service = new(h.States, h.Authority, h.Resolver, h.Recovery, new(h.Factory, h.Time), () => h.ReadOnly, clock)
                { Enabled = true, Limit = limit ?? TimeSpan.FromMinutes(10) };
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
        public event Action? Changed;
        public void Change() => Changed?.Invoke();
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
        public bool HoldResume;
        public TaskCompletionSource ResumeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SignInResume> ReleaseResume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> HasModelEvidenceAsync(SignInTarget target, CancellationToken ct) => Task.FromResult(ModelEvidence);
        public Task<SignInResume> ResumeAsync(SignInContext context, Func<bool> authorize, CancellationToken ct)
        {
            Assert.True(authorize());
            ++Resumes;
            ResumeEntered.TrySetResult();
            return HoldResume ? ReleaseResume.Task.WaitAsync(ct) : Task.FromResult(Result);
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
        public bool BlockSends;
        public int PendingSends;
        public CancellationToken HeldToken;
        public TaskCompletionSource SendBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSends { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
        {
            if (BlockSends) { return HoldAsync(ct); }
            var value = Json(data!);
            lock (Events) { Events.Add((name, value)); }
            if (name == AgentSignIn.Code) { Code.TrySetResult(value); }
            if (name == AgentSignIn.Done) { Done.TrySetResult(value); }
            return Task.FromResult(1L);
        }
        async Task<long> HoldAsync(CancellationToken ct)
        {
            HeldToken = ct;
            Interlocked.Increment(ref PendingSends);
            SendBlocked.TrySetResult();
            try
            {
                await ReleaseSends.Task.WaitAsync(ct);
                return 1L;
            }
            finally { Interlocked.Decrement(ref PendingSends); SendFinished.TrySetResult(); }
        }
        public void Close() => closed.TrySetResult();
    }
    sealed class ManualSweepClock(FakeTimeProvider inner) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == Timeout.InfiniteTimeSpan ? inner.CreateTimer(callback, state, dueTime, period) : new DisabledTimer();
        sealed class DisabledTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
