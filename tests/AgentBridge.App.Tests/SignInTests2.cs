using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;
using static SignInTests;

// Tests of SignInTests moved here, the slowest per line of them, so no one class is a test shard's long pole (helpers stay there).
public sealed class SignInTests2
{
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_second_recovery_within_the_minute_gets_its_own_words_and_count()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        await demo.Host.ClearSignInAsync();
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Recovered by itself"), "recovered by itself");
        // A new episode: needed again, then a person here signs in and the host clears them.
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "needed again");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await demo.Host.FinishSignInAsync("signedIn");
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in \u00b7 waiting for 3 sessions to confirm"), "waiting to confirm");
        await demo.Host.ConfirmSignInAsync();
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in \u00b7 3 sessions recovered"), "its own recovery", why: () => string.Join(" | ", demo.Main.SignIns.Select(b => b.Line)));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_code_that_came_before_a_slow_starts_result_is_still_there_when_the_result_comes()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // The code comes at once; the start's result 11 s later, within its timeout.
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(11);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "the code held for the start");
        await Task.Delay(TimeSpan.FromSeconds(10.3));
        // Past the ten seconds, the banners are drawn again (and what is held pruned): the code stays.
        TestUi.Read(() => { for (var i = 0; i < 5; i++) { demo.Main.Tick(); } return 0; });
        Assert.Equal(1, TestUi.Read(() => demo.Main.SignInCodesHeld));
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code", patience: TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_late_code_from_a_cancelled_machine_is_never_held_while_another_machine_starts()
    {
        await using var fleet = new Fleeted(two: true);
        await fleet.Host.NeedSignInAsync();
        await fleet.Other!.NeedSignInAsync();
        fleet.Join();
        await Wait.UntilAsync(() => fleet.BannerOn(DemoHost.RemoteId) is { HasVerb: true } && fleet.BannerOn(Fleeted.OtherId) is { HasVerb: true }, "both banners");
        // Build server: its code comes late, after the person has cancelled.
        fleet.Host.NextCodeDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { fleet.BannerOn(DemoHost.RemoteId)!.Press(); return 0; });
        await Wait.UntilAsync(() => fleet.Host.SignInLog.Count == 1 && fleet.Main.SignInSheet is not null, "the first sheet");
        await Task.Delay(300);
        TestUi.Read(() => { fleet.Main.CancelSignIn(); return 0; });
        // Build server 2 starts, its result slow: its own code is held for it, and nothing else.
        fleet.Other.NextStartDelay = TimeSpan.FromSeconds(4);
        await Wait.UntilAsync(() => fleet.BannerOn(Fleeted.OtherId) is { HasVerb: true, Busy: false }, "Sign in on the other");
        TestUi.Read(() => { fleet.BannerOn(Fleeted.OtherId)!.Press(); return 0; });
        await Wait.UntilAsync(() => fleet.Main.SignInCodesHeld == 1, "the other's own code held");
        await Task.Delay(TimeSpan.FromSeconds(1.8));
        Assert.Equal(1, TestUi.Read(() => fleet.Main.SignInCodesHeld));
        await Wait.UntilAsync(() => fleet.Main.SignInSheet is { HasCode: true }, "the other's code", patience: TimeSpan.FromSeconds(6));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_signed_in_hold_with_no_pending_ends_on_its_second_and_the_banner_follows_the_host_again()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await demo.Host.SignInUnderWayAsync(true);
        await demo.Host.FinishSignInAsync("signedIn", pendingFirst: false);
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet to close");
        Assert.Equal("Sign in", TestUi.Read(() => demo.Main.SignIns.Single().VerbText));
        await Task.Delay(AuthState.SignedInHold + TimeSpan.FromMilliseconds(200));
        // The first tick: not the five-second sweep, so only the hold's own deadline can end it.
        TestUi.Read(() => { demo.Main.Tick(); return 0; });
        Assert.Equal(("Waiting for sign-in", "Show code"), TestUi.Read(() => (demo.Main.SignIns.Single().Line, demo.Main.SignIns.Single().VerbText)));
    }

    [Theory]
    [Trait("Category", "Network")]
    [InlineData("WDJB-MJHT", "https://github.com.evil.example/login/device")]
    [InlineData("WDJB-MJHT", "http://github.com/login/device")]
    [InlineData("wdjb-mjht", "https://github.com/login/device")]
    public async Task A_spoofed_link_or_code_is_refused_whole_with_nothing_to_open_or_copy(string userCode, string uri)
    {
        await using var demo = new Demo();
        demo.Host.NextUserCode = userCode;
        demo.Host.NextVerificationUri = uri;
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true }, "the refusal");
        var sheet = TestUi.Read(() => demo.Main.SignInSheet!);
        Assert.Equal(("", "", (Uri?)null), TestUi.Read(() => (sheet.Code, sheet.Page, sheet.OpenUri)));
        Assert.DoesNotContain("evil", TestUi.Read(demo.Shown));
        Assert.DoesNotContain("wdjb", TestUi.Read(demo.Shown));
    }
}
