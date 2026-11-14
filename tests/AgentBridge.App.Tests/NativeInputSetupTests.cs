using Xunit;

namespace AgentBridge.App.Tests;

public sealed class NativeInputSetupTests
{
    [Fact]
    public void A_background_window_fails_before_any_later_input()
    {
        var attempts = 0;
        var posted = false;
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            NativeInputSetup.RequireForeground(() => { attempts++; return false; },
                () => NativeInputSetup.Foreground("CoreWindow", "Microsoft account", "WWAHost"));
            posted = true;
        });
        Assert.Equal(2, attempts);
        Assert.False(posted);
        Assert.Equal("setup: window not foreground (class: \"CoreWindow\"; title: \"Microsoft account\"; process: \"WWAHost\")", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Acquiring_foreground_on_either_attempt_allows_the_case(int successOn)
    {
        var attempts = 0;
        NativeInputSetup.RequireForeground(() => ++attempts == successOn,
            () => throw new InvalidOperationException("Owner lookup must not run after success."));
        Assert.Equal(successOn, attempts);
    }

    [Fact]
    public void Foreground_diagnostics_keep_owner_fields_on_one_line() =>
        Assert.Equal("class: \"unknown\"; title: \"Microsoft\\r\\naccount\"; process: \"unknown\"",
            NativeInputSetup.Foreground(null, "Microsoft\r\naccount", ""));

    [Theory]
    [InlineData(10, 20, 10, 10, 7, 42, 7, 42, true)]
    [InlineData(0, 20, 10, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 0, 10, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 10, 10, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 20, 30, 10, 7, 42, 7, 42, false)]
    [InlineData(10, 20, 10, 30, 7, 42, 7, 42, false)]
    [InlineData(10, 20, 10, 10, 0, 42, 0, 42, false)]
    [InlineData(10, 20, 10, 10, 7, 42, 8, 42, false)]
    [InlineData(10, 20, 10, 10, 7, 0, 7, 0, false)]
    [InlineData(10, 20, 10, 10, 7, 42, 7, 43, false)]
    public void Posted_input_requires_a_focused_child_of_the_foreground_app_on_its_thread_and_process(
        int window, int focus, int root, int foreground, uint thread, uint process, uint windowThread, uint windowProcess, bool ready) =>
        Assert.Equal(ready, NativeInputSetup.TargetReady(window, focus, root, foreground, thread, process, windowThread, windowProcess));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Acquisition_activates_and_releases_before_verifying_ownership(bool attached)
    {
        var calls = new List<string>();
        var owned = false;
        var acquired = NativeInputSetup.AcquireForeground(
            () => { calls.Add("verify"); return owned; },
            () => { calls.Add("attach"); return attached; },
            () => { calls.Add("activate"); owned = true; },
            value => { Assert.Equal(attached, value); calls.Add("release"); });
        Assert.True(acquired);
        Assert.Equal(new[] { "verify", "attach", "activate", "release", "verify" }, calls);
    }

    [Fact]
    public void An_owned_window_needs_no_attachment_or_activation()
    {
        Assert.True(NativeInputSetup.AcquireForeground(() => true,
            () => throw new InvalidOperationException("No attach expected."),
            () => throw new InvalidOperationException("No activation expected."),
            _ => throw new InvalidOperationException("No release expected.")));
    }

    [Fact]
    public void Activation_failure_still_releases_the_attached_input_queue()
    {
        var released = false;
        var error = Assert.Throws<InvalidOperationException>(() =>
            NativeInputSetup.AcquireForeground(() => false, () => true,
                () => throw new InvalidOperationException("activation failed"),
                attached => { Assert.True(attached); released = true; }));
        Assert.True(released);
        Assert.Equal("activation failed", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Terminal_fallback_runs_once_between_attempts_and_never_allows_unowned_input(int successOn)
    {
        var calls = new List<string>();
        var attempts = 0;
        var owned = false;
        var posted = false;
        var error = Record.Exception(() =>
        {
            NativeInputSetup.RequireForeground(() => NativeInputSetup.AcquireForeground(
                () => owned, () => { calls.Add("attach"); return true; },
                () => { calls.Add("activate"); owned = ++attempts == successOn; },
                _ => calls.Add("release")),
                () => NativeInputSetup.Foreground("CASCADIA_HOSTING_WINDOW_CLASS", @"C:\Windows\system32\wsl.exe", "WindowsTerminal"),
                () => calls.Add("minimize"));
            posted = true;
        });
        Assert.Equal(Math.Min(successOn, 2), attempts);
        Assert.Equal(successOn <= 2, posted);
        Assert.Equal(successOn == 1
            ? new[] { "attach", "activate", "release" }
            : new[] { "attach", "activate", "release", "minimize", "attach", "activate", "release" }, calls);
        if (successOn <= 2) { Assert.Null(error); }
        else
        {
            var failure = Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("setup: window not foreground (class: \"CASCADIA_HOSTING_WINDOW_CLASS\"; title: \"C:\\\\Windows\\\\system32\\\\wsl.exe\"; process: \"WindowsTerminal\")", failure.Message);
        }
    }

    [Fact]
    public void Release_failure_blocks_input_without_retrying()
    {
        var attempts = 0;
        var posted = false;
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            NativeInputSetup.RequireForeground(() => NativeInputSetup.AcquireForeground(
                () => false, () => true, () => attempts++,
                _ => throw new InvalidOperationException("release failed")), () => "unused",
                () => throw new InvalidOperationException("No retry expected."));
            posted = true;
        });
        Assert.False(posted);
        Assert.Equal(1, attempts);
        Assert.Equal("release failed", error.Message);
    }

    [Theory]
    [InlineData(true, "CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", true)]
    [InlineData(true, "CASCADIA_HOSTING_WINDOW_CLASS", "windowsterminal", true)]
    [InlineData(false, "CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", false)]
    [InlineData(true, "OtherWindow", "WindowsTerminal", false)]
    [InlineData(true, "CASCADIA_HOSTING_WINDOW_CLASS", "OtherProcess", false)]
    [InlineData(true, null, null, false)]
    public void Only_the_hosted_arm64_terminal_owner_can_be_minimized(bool hostedArm64, string? windowClass, string? process, bool allowed) =>
        Assert.Equal(allowed, NativeInputSetup.CanMinimizeTerminal(hostedArm64, windowClass, process));
}
