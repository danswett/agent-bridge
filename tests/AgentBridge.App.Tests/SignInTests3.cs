using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;
using static SignInTests;

// Tests of SignInTests moved here, the slowest per line of them, so no one class is a test shard's long pole (helpers stay there).
public sealed class SignInTests3
{
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_held_code_that_runs_out_before_its_starts_result_leaves_only_that_it_did()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextCodeLife = TimeSpan.FromSeconds(1.5);
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(4);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "the code held for the start");
        await Task.Delay(TimeSpan.FromSeconds(2));
        TestUi.Read(() => { for (var i = 0; i < 5; i++) { demo.Main.Tick(); } return 0; });
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        // Once the start's result comes, the sheet says the code ran out and offers Try again.
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Expired: true, HasCode: false, CanRetry: true }, "the expired sheet", patience: TimeSpan.FromSeconds(6),
            why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_start_refused_with_no_flow_drops_a_cancelled_presss_late_code_at_once()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // Press A: its code comes late, after the person has cancelled.
        demo.Host.NextCodeDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Host.SignInLog.Count == 1 && demo.Main.SignInSheet is not null, "the first sheet");
        await Task.Delay(300);
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Press B: slow, and refused with no flow. A's late code comes while it starts, and is held for it.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        demo.Host.NextStartRefusal = "this credential store is busy";
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(4);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "A's late code held while B starts");
        // The host's reason in the app's words, never its own (ui.md §6.13).
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null && demo.Banner is { Line: "Sign-in refused \u00b7 another sign-in is under way" }, "B refused, the sheet closed", patience: TimeSpan.FromSeconds(8),
            why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}; banner {demo.Banner?.Line}");
        // At once, not after the ten seconds an early event is otherwise held.
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_start_that_names_another_flow_drops_a_cancelled_presss_late_code_once_its_own_are_replayed()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // Press A: its code comes late, after the person has cancelled.
        demo.Host.NextCodeDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Host.SignInLog.Count == 1 && demo.Main.SignInSheet is not null, "the first sheet");
        await Task.Delay(300);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Press B: slow, and started in a flow of its own. Its code and A's late one are both held for it.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(3);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 2, "B's code and A's late one held while B starts");
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "B's own code", patience: TimeSpan.FromSeconds(8));
        // B's code is its flow's; A's, which nothing can claim now, is gone at once.
        Assert.Equal(1, TestUi.Read(() => demo.Main.SignInCodesHeld));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_press_whose_wait_was_cancelled_leaves_the_next_press_still_waiting_for_the_slow_cancel()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        demo.Host.NextCancelDelay = TimeSpan.FromSeconds(2);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Pressed again, and cancelled while it waits for the slow cancel.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Task.Delay(200);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Pressed a third time: the old cancel still hasn't finished, and this press waits for it too.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in a third time");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true } && demo.Host.SignInLog.Count == 4, "the new code", patience: TimeSpan.FromSeconds(8), why: () => string.Join(",", demo.Host.SignInLog));
        Assert.Equal([AuthState.Start, AuthState.Cancel, "cancelled", AuthState.Start], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Try_again_given_up_while_its_cancel_finishes_closes_the_sheet_and_sends_nothing()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextVerificationUri = "https://github.com.evil.example/login/device";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true, CanRetry: true }, "the refused sheet");
        demo.Host.NextCancelDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { demo.Main.RetrySignIn(); return 0; });
        await Task.Delay(200);
        // The host stops offering this device sign-in while the cancel finishes.
        await demo.Host.SignInWayAsync(null);
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet closed, not left on Starting", patience: TimeSpan.FromSeconds(5),
            why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}; log {string.Join(",", demo.Host.SignInLog)}");
        await Task.Delay(300);
        Assert.Equal([AuthState.Start, AuthState.Cancel, "cancelled"], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_code_that_runs_out_is_dropped_and_the_sheet_offers_Try_again()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextCodeLife = TimeSpan.FromSeconds(2);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await Task.Delay(2200);
        TestUi.Read(() => { for (var i = 0; i < 5; i++) { demo.Main.Tick(); } return 0; });
        Assert.True(TestUi.Read(() => demo.Main.SignInSheet is { Expired: true, HasCode: false, CanRetry: true }));
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        Assert.Contains("Sign-in code expired for Copilot on Build server", demo.Said);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Signed_in_before_the_host_says_pending_never_flashes_Show_code_and_nothing_is_said_twice()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        // Another sign-in is under way at the host (signingIn), and this one's done comes before its pending.
        await demo.Host.SignInUnderWayAsync(true);
        await demo.Host.FinishSignInAsync("signedIn", pendingFirst: false);
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet to close");
        var lines = TestUi.Read(() => demo.Main.SignIns.Select(b => (b.Line, b.VerbText)).ToList());
        Assert.Equal([("Signed in · waiting for 3 sessions to confirm", "Sign in")], lines);

        await demo.Host.SignedInPendingAsync();
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => !b.Needed), "confirming");
        Assert.DoesNotContain(TestUi.Read(() => demo.Main.SignIns.Select(b => b.VerbText).ToList()), v => v == "Show code");
        Assert.Single(demo.Said, "Copilot on Build server: Signed in · waiting for 3 sessions to confirm");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_that_fails_sign_in_again_while_confirming_goes_back_to_Needs_sign_in()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await demo.Host.FinishSignInAsync("signedIn");
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in · waiting for 3 sessions to confirm"), "waiting to confirm");

        await demo.Host.FailSignInAgainAsync();
        await Wait.UntilAsync(() => demo.Banner is { ListsSessions: true, HasVerb: true } b && b.Line.StartsWith("3 sessions affected: ", StringComparison.Ordinal), "needed again", why: () => string.Join(" | ", demo.Main.SignIns.Select(b => b.Line)));
        Assert.True(TestUi.Read(() => DemoHost.SignInSessions.All(id => demo.Main.Sessions.Single(c => c.Id == id).Line.FirstFact == "Needs sign-in")));
    }
}
